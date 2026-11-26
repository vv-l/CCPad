"""Task token accounting from existing CLI metadata. Never invokes a model."""
from __future__ import annotations

import json
import os
from contextlib import closing
from pathlib import Path
import sqlite3
import threading


TOKEN_FIELDS = ("input_tokens", "output_tokens", "cache_read", "cache_create")


def totals(raw: dict | None) -> dict | None:
    """Canonical counts: input includes cached input; cache is a subset."""
    if not isinstance(raw, dict) or "input_tokens" not in raw or "output_tokens" not in raw:
        return None
    if any(isinstance(raw.get(key), bool) for key in ("input_tokens", "output_tokens")):
        return None
    if any(isinstance(raw.get(key), bool) for key in
           ("cached_input_tokens", "cache_read", "cache_write_input_tokens", "cache_create")):
        return None
    try:
        result = {
            "input_tokens": int(raw["input_tokens"]),
            "output_tokens": int(raw["output_tokens"]),
            "cache_read": int(raw.get("cached_input_tokens", raw.get("cache_read", 0))),
            "cache_create": int(raw.get("cache_write_input_tokens", raw.get("cache_create", 0))),
        }
    except (TypeError, ValueError, OverflowError):
        return None
    if any(n < 0 for n in result.values()):
        return None
    if result["cache_read"] + result["cache_create"] > result["input_tokens"]:
        return None
    return result


def zero_usage() -> dict:
    return dict.fromkeys(TOKEN_FIELDS, 0)


class CodexUsageReader:
    """Indexed rollout lookup + bounded tail reads, never a history scan."""

    def __init__(self, home: str | Path | None = None, max_tail_bytes: int = 2 * 1024**2):
        self.home = Path(home or os.environ.get("CODEX_HOME") or Path.home()/".codex").resolve()
        self.max_tail_bytes = max_tail_bytes

    def snapshot(self, session_id: str | None) -> dict:
        try:
            return self._snapshot(session_id)
        except Exception:
            return {"usage": None, "model": "", "session_id": session_id or ""}

    def _snapshot(self, session_id: str | None) -> dict:
        result = {"usage": None, "model": "", "session_id": session_id or ""}
        if not session_id:
            return result
        # This is Codex's existing local index. Read-only, with a short lock
        # timeout; unavailable metadata must never prevent the task running.
        indexes = sorted(self.home.glob("state_*.sqlite"), reverse=True)
        for index in indexes:
            try:
                with closing(sqlite3.connect(index.as_uri()+"?mode=ro", uri=True, timeout=0.1)) as db:
                    row = db.execute("SELECT rollout_path, model FROM threads WHERE id=?", (session_id,)).fetchone()
                if not row:
                    continue
                path = Path(row[0]).resolve()
                result["model"] = row[1] or ""
                if not any(path.is_relative_to(self.home/name) for name in ("sessions", "archived_sessions")):
                    return result
                with path.open("rb") as stream:
                    stream.seek(0, 2)
                    size = stream.tell()
                    amount = min(128 * 1024, self.max_tail_bytes)
                    while amount:
                        offset = max(0, size-amount)
                        stream.seek(offset)
                        if offset:
                            stream.readline()  # discard an incomplete first row
                        lines = stream.read(amount).splitlines()
                        for line in reversed(lines):
                            if b'"token_count"' not in line or b'"total_token_usage"' not in line:
                                continue
                            try:
                                event = json.loads(line)
                            except (ValueError, UnicodeDecodeError):
                                continue
                            if not isinstance(event, dict):
                                continue
                            payload = event.get("payload") or {}
                            if not isinstance(payload, dict):
                                continue
                            if event.get("type") != "event_msg" or payload.get("type") != "token_count":
                                continue
                            info = payload.get("info") or {}
                            if not isinstance(info, dict):
                                continue
                            usage = totals(info.get("total_token_usage"))
                            if usage is not None:
                                result.update(usage=usage, observed_at=event.get("timestamp"))
                                # Throughput across requests is not the context
                                # window. Read both from the same bounded tail.
                                result["context_usage"] = totals(info.get("last_token_usage"))
                                try:
                                    window = int(info.get("model_context_window") or 0)
                                except (TypeError, ValueError):
                                    window = 0
                                result["model_context_window"] = window if window > 0 else None
                                return result
                        if offset == 0 or amount >= self.max_tail_bytes:
                            break
                        amount = min(amount*2, self.max_tail_bytes)
                return result
            except (OSError, sqlite3.Error, ValueError):
                continue
        return result


class CodexTaskUsage:
    def __init__(self, reader: CodexUsageReader, session_id: str | None):
        self.reader = reader
        self.session_id = session_id or ""
        before = reader.snapshot(session_id)
        self.baseline = before["usage"] if session_id else zero_usage()
        self.model = before["model"]
        self.started = False
        self.completed = False
        self.execution_started = False
        # True only when the app-server explicitly rejected turn/start for
        # capacity before a turn was accepted.  An EOF or an ambiguous
        # disconnect must remain non-retriable to avoid replaying side effects.
        self.capacity_retriable = False
        self.capacity_retry_exhausted = False
        self.cli_total = None
        self.session_changed = False

    def set_session(self, session_id: str) -> None:
        if self.session_id and self.session_id != session_id:
            # Do not assume an unexpected resumed thread starts at zero.
            self.baseline = None
            self.session_changed = True
        self.session_id = session_id

    def finish(self) -> dict:
        after = self.reader.snapshot(self.session_id)
        self.model = after["model"] or self.model
        cumulative = after["usage"]
        if self.completed and self.cli_total is not None:
            # Prefer the rollout's authoritative cumulative counter. If its
            # last row has not flushed yet, the CLI final is the fallback.
            if cumulative is None or cumulative == self.baseline:
                cumulative = self.cli_total
        task_usage = None
        complete = False
        reason = "usage_unavailable"
        if not self.started:
            task_usage, complete, reason = zero_usage(), True, "not_started"
        elif self.baseline is None:
            reason = "session_changed" if self.session_changed else "baseline_unavailable"
        elif not self.completed and cumulative == self.baseline:
            # No new report is not proof that an interrupted request cost zero.
            reason = "no_new_reported_usage"
        elif cumulative is not None:
            delta = {key: cumulative[key]-self.baseline[key] for key in TOKEN_FIELDS}
            task_usage = totals(delta)
            if task_usage is None:
                reason = "counter_reset_or_invalid_delta"
            else:
                complete = self.completed and self.cli_total is not None
                reason = "complete" if complete else "partial_reported_usage"
        event = {
            "type": "usage_final", "usage_scope": "task",
            "task_usage": task_usage, "session_usage": cumulative,
            "usage_complete": complete, "usage_reason": reason,
            "session_id": self.session_id, "model": self.model,
            "input_includes_cache": True,
        }
        context = after.get("context_usage")
        if context is not None:
            # Latest request input already includes cache. Include its output
            # because it becomes part of the next request's retained context.
            event["context_tokens"] = context["input_tokens"] + context["output_tokens"]
            event["model_context_window"] = after.get("model_context_window")
        if task_usage is not None:
            event.update(task_usage)
        return event


class TaskUsageCollector:
    """Captures task totals before UI callbacks, including legacy Claude events."""

    def __init__(self):
        self.final = None
        self.calls = zero_usage()
        self.seen_calls = set()
        self.observed = False

    @staticmethod
    def _claude_counts(event: dict) -> dict | None:
        raw = dict(event)
        try:
            raw["input_tokens"] = sum(int(event.get(k, 0) or 0) for k in ("input_tokens", "cache_read", "cache_create"))
        except (TypeError, ValueError):
            return None
        return totals(raw)

    def observe(self, event: dict) -> dict:
        if event.get("type") not in ("usage", "usage_final"):
            return event
        if event.get("usage_scope") == "task":
            self.final = dict(event)
            return event
        usage = self._claude_counts(event)
        if usage is None:
            return event
        self.observed = True
        if event["type"] == "usage":
            call_id = event.get("usage_id")
            # Without a message id, do not invent an exact sum. Keep a known
            # reported call as a lower bound until an authoritative final.
            if call_id and call_id not in self.seen_calls:
                self.seen_calls.add(call_id)
                for key in TOKEN_FIELDS:
                    self.calls[key] += usage[key]
            elif not call_id:
                for key in TOKEN_FIELDS:
                    self.calls[key] = max(self.calls[key], usage[key])
            return event  # context-budget guard still needs per-call semantics
        self.final = {**event, **usage, "usage_scope": "task", "task_usage": usage,
                      "input_includes_cache": True, "usage_complete": True,
                      "usage_reason": "complete", "session_usage": None}
        return self.final

    def result(self) -> dict:
        if self.final is not None:
            return self.final
        result = {"type": "usage_final", "usage_scope": "task",
                "task_usage": self.calls.copy() if self.observed else None,
                "session_usage": None, "usage_complete": False,
                "usage_reason": "partial_reported_usage" if self.observed else "usage_unavailable",
                "input_includes_cache": True}
        if result["task_usage"] is not None:
            result.update(result["task_usage"])
        return result


class TaskUsageLedger:
    """One small append per completed attempt; no database migration or LLM."""

    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.lock = threading.Lock()

    def append(self, record: dict) -> bool:
        try:
            line = json.dumps({"schema_version": 1, **record}, ensure_ascii=False, separators=(",", ":"))+"\n"
            with self.lock:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                with self.path.open("a", encoding="utf-8") as stream:
                    stream.write(line)
                    stream.flush()
                    os.fsync(stream.fileno())
            return True
        except (OSError, ValueError, TypeError) as exc:
            # No model rerun, no false 'saved' flag, and no response blockage.
            print(f"[TaskUsage] save failed task={record.get('task_id')}: {type(exc).__name__}", flush=True)
            return False


def format_task_usage(event: dict | None) -> str:
    if event is None:
        return ""
    usage = event.get("task_usage")
    # Keep accepting the original Codex runner's final event shape, which
    # carried input/output at the top level and did not include task_usage.
    # The newer collector supplies task_usage explicitly, but old cards and
    # direct runner integrations still send the legacy shape.
    if usage is None and "input_tokens" in event and "output_tokens" in event:
        usage = totals(event)
    complete = event.get("usage_complete")
    if complete is None and usage is not None:
        complete = True
    label = "本次任务" if complete else "本次任务（统计不完整）"
    if usage is None:
        line = f"{label}：用量暂不可用"
    else:
        line = f"{label}：输入 {usage['input_tokens']:,} · 输出 {usage['output_tokens']:,}"
        if usage.get("cache_read") or usage.get("cache_create"):
            line += f" · 其中缓存 {usage.get('cache_read', 0)+usage.get('cache_create', 0):,}"
    session = event.get("session_usage")
    if event.get("total_cost_usd"):
        line += f" · ${event['total_cost_usd']:.4f}"
    if session is not None:
        line += f"\n会话累计：输入 {session['input_tokens']:,} · 输出 {session['output_tokens']:,}"
    return line


def format_task_usage_progress(event: dict | None) -> str:
    """Format a non-authoritative live Codex usage update for a card.

    Codex's JSONL protocol reports usage at turn completion. That value is
    useful for showing activity while the task is still open, but it must not
    be used as the context-window estimate or as the final rollout delta.
    """
    if not event:
        return ""
    usage = event.get("task_usage")
    if usage is None:
        usage = totals(event)
    if usage is None:
        return ""
    line = f"实时用量（暂估）：输入 {usage['input_tokens']:,} · 输出 {usage['output_tokens']:,}"
    if usage.get("cache_read") or usage.get("cache_create"):
        line += f" · 其中缓存 {usage.get('cache_read', 0)+usage.get('cache_create', 0):,}"
    return line


if __name__ == "__main__":
    import argparse
    from collections import deque
    parser = argparse.ArgumentParser(description="Read saved task token usage; no model or network call")
    parser.add_argument("--task-id", default="")
    parser.add_argument("--limit", type=int, default=20)
    args = parser.parse_args()
    path = Path(__file__).parent/"data/task_token_usage.jsonl"
    records = deque(maxlen=max(1, args.limit))
    if path.exists():
        with path.open(encoding="utf-8") as stream:
            for line in stream:
                try:
                    record = json.loads(line)
                except ValueError:
                    continue
                if not args.task_id or record.get("task_id") == args.task_id:
                    records.append(record)
    print(json.dumps(list(records), ensure_ascii=False, indent=2))
