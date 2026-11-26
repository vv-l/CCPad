"""CardKit-based streaming card for Feishu.

This module is the new, high-fidelity card UX based on Feishu's CardKit API
(`cardkit.v1.cardElement.content`), which bypasses the ~600ms `im.message.patch`
latency with a purpose-built streaming API (~100ms cadence).

Ported from `larksuite/openclaw-lark` (TypeScript) patterns:
  - schema 2.0 card with `streaming_mode: true`
  - collapsible_panel for tool calls and thinking
  - cumulative full-text updates via CardKit
  - markdown style optimization

Falls back to legacy `im.message.patch` if CardKit is unavailable
(permission / network / server error).
"""

import json
from datetime import datetime, timezone, timedelta
from pathlib import Path
from hashlib import sha256
import os
import re
import threading
from functools import wraps
import time
from numbers import Real
from typing import Any, Callable, Dict, List, Optional

import lark_oapi as lark
from lark_oapi.api.im.v1 import (
    CreateMessageRequest,
    CreateMessageRequestBody,
    ReplyMessageRequest,
    ReplyMessageRequestBody,
)
from lark_oapi.api.cardkit.v1 import (
    CreateCardRequest,
    CreateCardRequestBody,
    ContentCardElementRequest,
    ContentCardElementRequestBody,
    UpdateCardElementRequest,
    UpdateCardElementRequestBody,
    SettingsCardRequest,
    SettingsCardRequestBody,
)


# ===== Element IDs (stable names the CardKit API references) =====
EL_TOOL_PANEL: str = "tool_panel"
EL_THINKING_PANEL: str = "thinking_panel"
EL_STREAMING: str = "streaming_content"
EL_FOOTER: str = "footer"
EL_STOP_BTN: str = "stop_button"
EL_ADVANCE_BTN: str = "advance_queue_button"
EL_FEEDBACK_FORM: str = "feedback_form"

# Kept for the quarantined Codex snapshot adapter and its offline fixtures.
# The active renderer below never inserts these elements into a task card.
EL_PHASE: str = "codex_phase"
EL_PROCESS_PANEL: str = "codex_process"
EMPTY_TOOLS = "本分片尚无工具调用；续卡计时不代表有新的执行进度。"
EMPTY_THINKING = "引擎尚未提供可显示的思考摘要。"


# ===== Throttle constants (ported from openclaw-lark) =====
CARDKIT_MS: int = 2000       # CardKit streaming cadence (text typewriter)
# NOTE(2026-06-15): raised 100 -> 2000. Each Feishu CardKit element update call
# actually costs ~2-3s server-side (measured avg 2971ms, max 16183ms; network to
# open.feishu.cn is only ~130ms, so it's CardKit endpoint latency, not the proxy).
# At the old 100ms cadence the streamer flushed on essentially every delta, so the
# card lagged minutes behind the agent and looked "stuck". Throttle must approach
# the real call duration to actually coalesce deltas into fewer calls.
PATCH_MS: int = 1500         # fallback IM patch cadence
LONG_GAP_MS: int = 2000      # "long gap" after which we delay batches
BATCH_AFTER_GAP_MS: int = 300

# Card rotation: Feishu enforces a 10-minute absolute timeout on streaming cards.
# We proactively rotate at 9 minutes to avoid hitting 200850 errors.
CARD_ROTATE_SECONDS: int = 540   # 9 minutes

# Per-call RTT logging for CardKit API. Set FEISHUBOT_PERF=0 to disable.
PERF_LOG: bool = os.environ.get("FEISHUBOT_PERF", "1") != "0"


def queue_handoff_text(waiting_text: str) -> str:
    """Keep the task preview and replace the stale queue state with one notice."""
    _, marker, preview = waiting_text.partition("\n\n> 任务：")
    notice = "▶️ 已开始执行，最新进展见下方新卡片。"
    return f"> 任务：{preview}\n\n{notice}" if marker and preview else notice


def format_duration(seconds: Any) -> str:
    """Format a duration for card text without exposing decimal seconds.

    CardKit status is user-facing, so keep the units compact and consistent:
    seconds below a minute, minutes plus seconds below an hour, and hours plus
    minutes plus seconds for longer tasks.  Invalid and negative values are
    treated as zero; raw event values remain untouched in logs and state.
    """
    try:
        if isinstance(seconds, bool):
            raise ValueError("boolean is not a duration")
        total = max(0, int(float(seconds)))
    except (TypeError, ValueError, OverflowError):
        total = 0

    hours, remainder = divmod(total, 3600)
    minutes, secs = divmod(remainder, 60)
    if hours:
        return f"{hours} 小时 {minutes} 分 {secs} 秒"
    if minutes:
        return f"{minutes} 分 {secs} 秒"
    return f"{secs} 秒"


# ===== Tool icon token mapping (from openclaw-lark) =====
TOOL_ICONS: Dict[str, str] = {
    "Read":      "file-link-text_outlined",
    "Write":     "edit-text_outlined",
    "Edit":      "edit-text_outlined",
    "NotebookEdit": "edit-text_outlined",
    "Bash":      "setting_outlined",
    "BashOutput": "setting_outlined",
    "Grep":      "search_outlined",
    "Glob":      "search_outlined",
    "WebSearch": "search-dingtalk_outlined",
    "WebFetch":  "earth_outlined",
    "Task":      "robot-ai_outlined",
    "TodoWrite": "list-group-list_outlined",
    "ToolSearch": "search_outlined",
    "Skill":     "robot-ai_outlined",
}


# ===== Markdown style optimizer (ported from openclaw-lark markdown-style.ts) =====

def optimize_markdown_for_feishu(text: str) -> str:
    """Adjust markdown so it renders well in Feishu cards.

    - Demote # headings (Feishu renders them too big)
    - Add <br> spacing around tables and code blocks
    - Normalize consecutive blank lines
    """
    if not text:
        return text

    lines: List[str] = text.split("\n")
    out: List[str] = []
    in_code: bool = False

    for i, line in enumerate(lines):
        stripped = line.strip()

        # Track code fence state
        if stripped.startswith("```"):
            if not in_code:
                # Opening fence: add spacing before
                if out and out[-1].strip():
                    out.append("<br>")
            else:
                # Closing fence: add spacing after (will be inserted below)
                pass
            in_code = not in_code
            out.append(line)
            if not in_code:
                out.append("<br>")
            continue

        if in_code:
            out.append(line)
            continue

        # Demote headings
        if re.match(r"^#\s+", line):
            line = "####" + line[1:]
        elif re.match(r"^##\s+", line):
            line = "#####" + line[2:]
        elif re.match(r"^#{3,}\s+", line):
            line = "#####" + re.sub(r"^#+", "", line)

        out.append(line)

    return "\n".join(out)


# ===== Shard splitting for long final answers =====

CARD_SHARD_THRESHOLD: int = 5000  # split final text into multiple cards beyond this


def _line_start_fences(window: str) -> List[int]:
    """Return indices of ``` that start a line within window."""
    out: List[int] = []
    i = 0
    while True:
        j = window.find("```", i)
        if j == -1:
            break
        if j == 0 or window[j - 1] == "\n":
            out.append(j)
        i = j + 3
    return out


def _find_safe_cut(text: str, max_len: int) -> int:
    """Pick a cut index <= max_len. Prefers paragraph / line boundaries and
    tries to keep whole code blocks in one shard.
    """
    window = text[:max_len]
    fences = _line_start_fences(window)
    in_code = (len(fences) % 2) == 1

    if in_code:
        # Back up to just before the last opening fence so the whole code
        # block moves to the next shard intact — but only if that leaves us
        # with a usable amount of content (at least 30% of max_len).
        last_open = fences[-1]
        if last_open >= int(max_len * 0.3):
            return last_open
        # Otherwise the code block itself is too big to defer; fall through
        # and cut inside the block. The caller will close/reopen the fence.

    lower_bound = int(max_len * 0.5)

    idx = text.rfind("\n\n", lower_bound, max_len)
    if idx != -1:
        return idx + 2

    idx = text.rfind("\n", lower_bound, max_len)
    if idx != -1:
        return idx + 1

    return max_len


def _extract_fence_lang(text: str, fence_idx: int) -> str:
    """Given the position of a ``` that opens a code block, return its
    language tag (text after the fence, before the newline)."""
    end = text.find("\n", fence_idx + 3)
    if end == -1:
        return ""
    return text[fence_idx + 3:end].strip()


def split_for_shards(text: str, max_len: int = CARD_SHARD_THRESHOLD) -> List[str]:
    """Split long text into shards of roughly max_len chars.

    - Prefers paragraph (\\n\\n) then line (\\n) boundaries.
    - Tries to keep whole fenced code blocks in one shard.
    - If a single code block is larger than max_len, it will be cut and the
      cut shard is closed with ``` while the next shard reopens with the
      same language tag — so each shard renders with balanced fences.
    """
    if not text or len(text) <= max_len:
        return [text or ""]

    shards: List[str] = []
    remaining = text
    # Track if we're currently inside an open code block carried over from
    # the previous shard, and the language tag to reopen with.
    carry_fence_lang: Optional[str] = None

    while len(remaining) > max_len:
        # If we carried an open fence from the previous shard, reopen it at
        # the start of this shard's text before measuring.
        if carry_fence_lang is not None:
            reopen = f"```{carry_fence_lang}\n"
            remaining = reopen + remaining
            carry_fence_lang = None

        cut = _find_safe_cut(remaining, max_len)
        head = remaining[:cut]

        # Is head ending inside an open code block?
        fences = _line_start_fences(head)
        head_in_code = (len(fences) % 2) == 1
        if head_in_code:
            # Close the fence at the end of this shard so it renders cleanly;
            # remember the language tag to reopen on the next shard.
            open_idx = fences[-1]
            lang = _extract_fence_lang(head, open_idx)
            if not head.endswith("\n"):
                head = head + "\n"
            head = head + "```"
            carry_fence_lang = lang

        shards.append(head.rstrip())
        remaining = remaining[cut:].lstrip("\n")

    if carry_fence_lang is not None:
        reopen = f"```{carry_fence_lang}\n"
        remaining = reopen + remaining
    if remaining:
        shards.append(remaining)
    return shards


def strip_markdown(text: str, max_len: int = 120) -> str:
    """Produce a short plain-text preview for card summary field."""
    if not text:
        return ""
    # Crude: drop common markdown chars
    t = re.sub(r"[#*`_\[\]()!>~-]", "", text)
    t = re.sub(r"\s+", " ", t).strip()
    return t[:max_len]


# ===== Card schema builders =====

def _abort_button(chat_id: str, task_id: str = "") -> Dict[str, Any]:
    """Abort button — placed in a body-top right-aligned column_set.

    Schema 2.0 dropped `header.extra` button rendering (dead since the 1.0→2.0
    cutover; cards still validate but newer clients don't render). Official
    replacement pattern is column_set with weight:1 spacer + width:auto button.
    """
    return {
        "tag": "button",
        "element_id": EL_STOP_BTN,
        "text": {"tag": "plain_text", "content": "✕"},
        "type": "danger",
        "size": "small",
        "value": {
            "action": "abort",
            "chat_id": chat_id,
            "task_id": task_id,
        },
    }


def _advance_queue_button(chat_id: str, task_id: str = "") -> Dict[str, Any]:
    """End this task while retaining later FIFO tasks for execution."""
    return {
        "tag": "button",
        "element_id": EL_ADVANCE_BTN,
        "text": {"tag": "plain_text", "content": "⏭ 终止并继续"},
        "type": "primary",
        "size": "small",
        "value": {
            "action": "advance_queue",
            "chat_id": chat_id,
            "task_id": task_id,
        },
    }


def _abort_button_row(chat_id: str, task_id: str = "") -> Dict[str, Any]:
    """Original shared-card action row with the task-specific abort button."""
    return {
        "tag": "column_set",
        "flex_mode": "stretch",
        "columns": [
            {"tag": "column", "weight": 1, "elements": []},
            {
                "tag": "column",
                "width": "auto",
                "vertical_align": "center",
                "elements": [_abort_button(chat_id, task_id)],
            },
        ],
    }


def _feedback_form(chat_id: str, task_id: str = "") -> Dict[str, Any]:
    """Collect an optional user correction and steer the active turn.

    The form is deliberately part of the running card rather than a new chat
    message.  Feishu returns its fields as ``action.form_value`` while the
    immutable value keeps the exact chat/task binding for stale-card checks.
    """
    return {
        "tag": "form",
        "element_id": EL_FEEDBACK_FORM,
        "name": "feedback_form",
        "elements": [
            {
                "tag": "input",
                "name": "feedback_text",
                "placeholder": {
                    "tag": "plain_text",
                    "content": "补充信息或纠正方向（提交后继续当前任务）",
                },
                "max_length": 1000,
            },
            {
                "tag": "button",
                "element_id": "feedback_submit",
                "name": "feedback_submit",
                "action_type": "form_submit",
                "text": {"tag": "plain_text", "content": "反馈并继续"},
                "type": "default",
                "size": "small",
                "value": {
                    "action": "steer",
                    "chat_id": chat_id,
                    "task_id": task_id,
                },
            },
        ],
    }


def build_initial_card(title: str = "Claude 工作中",
                       chat_id: str = "", task_id: str = "",
                       engine_key: str = "claude") -> Dict[str, Any]:
    """Build the original shared streaming card (schema 2.0).

    ``engine_key`` remains accepted for compatibility with older callers, but
    the card layout is intentionally engine-agnostic.  Engine-specific events
    are rendered through the same tool/thinking/text elements below.
    """
    card = {
        "schema": "2.0",
        "config": {
            "streaming_mode": True,
            "summary": {"content": title},
        },
        "card_link": {},
        "header": {
            "title": {"tag": "plain_text", "content": title},
            "template": "blue",
        },
        "body": {
            "elements": [
                _abort_button_row(chat_id, task_id),
                {
                    "tag": "collapsible_panel",
                    "element_id": EL_TOOL_PANEL,
                    "expanded": True,
                    "header": {
                        "title": {
                            "tag": "plain_text",
                            "content": "🛠️ Tool use pending",
                            "text_color": "grey",
                            "text_size": "notation",
                        },
                        "icon": {
                            "tag": "standard_icon",
                            "token": "down-small-ccm_outlined",
                            "size": "14px 14px",
                        },
                    },
                    "border": {"color": "grey", "corner_radius": "5px"},
                    "elements": [],
                },
                {
                    "tag": "collapsible_panel",
                    "element_id": EL_THINKING_PANEL,
                    "expanded": False,
                    "header": {
                        "title": {
                            "tag": "plain_text",
                            "content": "💭 Thinking",
                            "text_color": "grey",
                            "text_size": "notation",
                        },
                        "icon": {
                            "tag": "standard_icon",
                            "token": "down-small-ccm_outlined",
                            "size": "14px 14px",
                        },
                    },
                    "border": {"color": "grey", "corner_radius": "5px"},
                    "elements": [],
                },
                {
                    "tag": "markdown",
                    "element_id": EL_STREAMING,
                    "content": " ",
                },
                {
                    "tag": "markdown",
                    "element_id": EL_FOOTER,
                    "content": "",
                    "text_size": "notation",
                },
            ]
        },
    }
    # Compatibility-only path for the quarantined snapshot reporter.  The
    # active main flow never sets ``engine_key`` to codex, so production keeps
    # the original shared card.  Keeping this adapter path lets its isolated
    # fixtures remain available for comparison and rollback analysis.
    if engine_key == "codex":
        elements = card["body"]["elements"]
        elements.insert(1, {"tag": "markdown", "element_id": EL_PHASE,
                            "content": "⏳ 已收到请求，正在启动 Codex。"})
        for element in elements:
            if element.get("element_id") == EL_TOOL_PANEL:
                element["header"]["title"]["content"] = "🛠️ 工具执行"
                element["elements"] = [{"tag": "markdown",
                                         "content": "等待工具调用；仅回答问题时可能不调用工具。",
                                         "text_size": "notation"}]
            elif element.get("element_id") == EL_THINKING_PANEL:
                element["expanded"] = True
                element["header"]["title"]["content"] = "💭 公开思考摘要"
                element["elements"] = [{"tag": "markdown",
                                         "content": "等待 Codex 提供公开思考摘要。",
                                         "text_size": "notation"}]
            elif element.get("element_id") == EL_STREAMING:
                element["content"] = "已收到请求，正在准备会话和工具…"
        elements.insert(-1, build_codex_panel(EL_PROCESS_PANEL, "📋 过程记录", "0 秒 · 已收到请求。"))
    return card


def build_codex_panel(element_id: str, title: str, text: str,
                      expanded: bool = True) -> Dict[str, Any]:
    """Compatibility helper for the quarantined Codex snapshot adapter.

    Production cards use the original shared layout; this helper is retained
    so old offline fixtures and a possible rollback package remain importable.
    """
    return {"tag": "collapsible_panel", "element_id": element_id, "expanded": expanded,
            "header": {"title": {"tag": "plain_text", "content": title,
                                 "text_color": "grey", "text_size": "notation"},
                       "icon": {"tag": "standard_icon", "token": "down-small-ccm_outlined",
                                "size": "14px 14px"}},
            "border": {"color": "grey", "corner_radius": "5px"},
            "elements": [{"tag": "markdown", "content": text, "text_size": "notation"}]}


def build_continuation_card(shard_idx: int, total: int, content: str) -> Dict[str, Any]:
    """Build a continuation (non-streaming) card for shard 2..N of a long
    final answer. Visually distinct from the main card:
      - wathet (pale-cyan) header instead of blue
      - title shows "↪ 接上文 · i/N"
      - no tool / thinking panels
      - body is prefixed with a quote block so it's clearly a continuation
    """
    opt = optimize_markdown_for_feishu(content)
    body_md = f"> ↪ 接自上一张卡 ⋯\n\n{opt}"
    return {
        "schema": "2.0",
        "config": {
            "streaming_mode": False,
            "summary": {"content": f"接上文 {shard_idx}/{total}"},
        },
        "card_link": {},
        "header": {
            "title": {
                "tag": "plain_text",
                "content": f"↪ 接上文 · {shard_idx}/{total}",
            },
            "template": "wathet",
        },
        "body": {
            "elements": [
                {
                    "tag": "markdown",
                    "content": body_md,
                },
                {
                    "tag": "markdown",
                    "content": f"第 {shard_idx}/{total} 片",
                    "text_size": "notation",
                },
            ],
        },
    }


def build_tool_step_element(action: str, icon_token: str = "setting_outlined") -> Dict[str, Any]:
    """Build a single tool call display element for the tool panel."""
    return {
        "tag": "div",
        "icon": {
            "tag": "standard_icon",
            "token": icon_token,
            "color": "grey",
        },
        "text": {
            "tag": "plain_text",
            "content": action,
            "text_color": "grey",
            "text_size": "notation",
        },
    }


# ===== CardKitStreamer =====

def _serialized_render(method):
    """Keep heartbeat, event rendering and terminal writes in one order."""
    @wraps(method)
    def render(self, *args, **kwargs):
        with self._lock:
            return method(self, *args, **kwargs)
    return render


class CardKitStreamer:
    """Drives a single Feishu card through CardKit's streaming API.

    Usage:
        streamer = CardKitStreamer(client, chat_id)
        streamer.start()  # creates the card and sends it
        streamer.set_streaming_text("Hello... world")  # streams to element
        streamer.add_tool_step("Read main.py", icon="file-link-text_outlined")
        streamer.set_thinking("I need to think about this...")
        streamer.set_footer("✅ Completed · 4.1s · in 1.2k · out 345")
        streamer.finalize(final_markdown)  # closes streaming mode, final replace
    """

    def __init__(self, client: lark.Client, chat_id: str,
                 title: str = "Claude 工作中", task_id: str = "",
                 reply_to: str = "", reply_in_thread: bool = False) -> None:
        self.client: lark.Client = client
        self.chat_id: str = chat_id
        self.title: str = title
        self.task_id: str = task_id
        self.reply_to: str = reply_to
        self.reply_in_thread: bool = reply_in_thread

        self.card_id: str = ""
        self.message_id: str = ""
        self.sequence: int = 0  # monotonic per-card counter
        self.tool_steps: List[Dict[str, Any]] = []  # accumulated dicts to patch into EL_TOOL_PANEL
        self.tool_total = 0
        self._tool_seen = set()
        self._tool_actions = {}
        self._tool_rows = {}
        self._recent_tool_ids = []
        self.progress_entries = []
        self.progress_total = 0
        self._progress_seen = {}
        self.streaming_text: str = ""
        self.thinking_text: str = ""
        self.footer_text: str = ""
        self.started: bool = False
        self.fallback_mode: bool = False  # True if CardKit failed; use legacy patch

        # Card rotation state
        self._task_start_time: float = 0.0  # time.time() when the task's first card was sent
        self._card_start_time: float = 0.0   # time.time() when current card was created
        self._card_generation: int = 0        # 0 = first card, 1 = first rotation, ...
        self._rotating: bool = False          # guard against re-entrant rotation
        self._streaming_expired: bool = False  # set by _cardkit_content on 200850/300309
        self._rotation_retry_at: float = 0.0

        self._lock: threading.Lock = threading.Lock()
        self._last_text_flush: float = 0.0
        self._last_panel_flush: float = 0.0

    @property
    def shard_number(self) -> int:
        """One-based number of the currently streamed card."""
        return self._card_generation + 1

    @property
    def current_shard_number(self) -> int:
        """Alias used by status renderers that call the card a shard."""
        return self.shard_number

    def task_elapsed_seconds(self, now: Optional[float] = None) -> float:
        """Return task lifetime across all card rotations."""
        if not self._task_start_time:
            return 0.0
        return max(0.0, (time.time() if now is None else now) - self._task_start_time)

    def current_shard_elapsed_seconds(self, now: Optional[float] = None) -> float:
        """Return lifetime of the currently visible streaming card."""
        if not self._card_start_time:
            return 0.0
        return max(0.0, (time.time() if now is None else now) - self._card_start_time)

    def reset_task_clock(self, now: Optional[float] = None) -> None:
        """Reset task time without hiding the existing card's server-side age."""
        timestamp = time.time() if now is None else now
        self._task_start_time = timestamp
        if not self._card_start_time:
            self._card_start_time = timestamp

    def _default_running_footer(self) -> str:
        return (
            f"⋯ 运行中 · 任务累计时长 {format_duration(self.task_elapsed_seconds())}"
            f" · 当前卡片时长 {format_duration(self.current_shard_elapsed_seconds())}"
            f" · 分片 {self.shard_number}"
        )

    def _next_seq(self) -> int:
        with self._lock:
            self.sequence += 1
            return self.sequence

    def _send_card_message(self, card_id: str):
        card_ref = {"type": "card", "data": {"card_id": card_id}}
        content = json.dumps(card_ref, ensure_ascii=False)
        if self.reply_to:
            try:
                body = (ReplyMessageRequestBody.builder()
                        .msg_type("interactive")
                        .content(content)
                        .reply_in_thread(self.reply_in_thread)
                        .build())
                req = (ReplyMessageRequest.builder()
                       .message_id(self.reply_to)
                       .request_body(body)
                       .build())
                resp = self.client.im.v1.message.reply(req)
                if resp.success() and resp.data and resp.data.message_id:
                    return resp
                print(f"[CardKit] reply card failed; fallback create: "
                      f"{resp.code} {resp.msg}", flush=True)
            except Exception as e:
                print(f"[CardKit] reply card exception; fallback create: {e}", flush=True)
        body = (CreateMessageRequestBody.builder()
                .receive_id(self.chat_id)
                .msg_type("interactive")
                .content(content)
                .build())
        req = (CreateMessageRequest.builder()
               .receive_id_type("chat_id")
               .request_body(body)
               .build())
        return self.client.im.v1.message.create(req)

    # ===== Phase 1: Create =====

    def start(self, initial_footer: Optional[str] = None) -> bool:
        """Create the card via CardKit + send the card message.

        Returns True on success, False if CardKit failed (will fall back).

        ``initial_footer`` is written immediately after the first card message
        is sent.  A generic running footer keeps direct Streamer users covered;
        CardKitReporter supplies the richer task/step/usage footer.
        """
        card = build_initial_card(
            self.title, chat_id=self.chat_id, task_id=self.task_id,
            engine_key=self.engine_key if hasattr(self, "engine_key") else "claude",
        )

        # Step 1: Create the card via CardKit
        try:
            req = (CreateCardRequest.builder()
                   .request_body(
                       CreateCardRequestBody.builder()
                       .type("card_json")
                       .data(json.dumps(card, ensure_ascii=False))
                       .build()
                   )
                   .build())
            t0 = time.time()
            resp = self.client.cardkit.v1.card.create(req)
            rtt_create_ms = int((time.time() - t0) * 1000)
            if not resp.success() or not resp.data or not resp.data.card_id:
                print(f"[CardKit] create failed: code={resp.code} msg={resp.msg}", flush=True)
                if PERF_LOG:
                    print(f"[CardKit-perf] card.create {rtt_create_ms}ms FAIL:{resp.code}", flush=True)
                self.fallback_mode = True
                return False
            self.card_id = resp.data.card_id
            if PERF_LOG:
                print(f"[CardKit-perf] card.create {rtt_create_ms}ms ok", flush=True)
        except Exception as e:
            print(f"[CardKit] create exception: {e}", flush=True)
            self.fallback_mode = True
            return False

        # Step 2: Send the card as an im message referencing the card_id
        try:
            t0 = time.time()
            resp = self._send_card_message(self.card_id)
            rtt_msg_ms = int((time.time() - t0) * 1000)
            if not resp.success() or not resp.data or not resp.data.message_id:
                print(f"[CardKit] send msg failed: code={resp.code} msg={resp.msg}", flush=True)
                if PERF_LOG:
                    print(f"[CardKit-perf] message.create {rtt_msg_ms}ms FAIL:{resp.code}", flush=True)
                self.fallback_mode = True
                return False
            self.message_id = resp.data.message_id
            self.started = True
            self._task_start_time = time.time()
            self._card_start_time = self._task_start_time
            self._card_generation = 0
            self._streaming_expired = False
            if PERF_LOG:
                print(f"[CardKit-perf] message.create {rtt_msg_ms}ms ok (card ready)", flush=True)
            footer = initial_footer if initial_footer is not None else self._default_running_footer()
            if footer:
                self.footer_text = footer
                self._cardkit_content(EL_FOOTER, footer)
            return True
        except Exception as e:
            print(f"[CardKit] send msg exception: {e}", flush=True)
            self.fallback_mode = True
            return False

    # ===== Phase 2: Stream updates =====

    def set_streaming_text(self, text: str) -> None:
        """Stream cumulative text to the main markdown element."""
        if not self.started or self.fallback_mode:
            return
        self.streaming_text = text

        # Throttle text flushes to CARDKIT_MS cadence
        now = time.time()
        if (now - self._last_text_flush) * 1000 < CARDKIT_MS:
            return
        self._last_text_flush = now

        self._cardkit_content(EL_STREAMING, optimize_markdown_for_feishu(text))

    def add_tool_step(self, action: str, icon: str = "setting_outlined") -> None:
        """Append a tool step to the tool panel."""
        if not self.started or self.fallback_mode:
            return
        self.tool_steps.append(build_tool_step_element(action, icon))
        self.tool_steps = self.tool_steps[-8:]
        self._flush_tool_panel()

    def set_thinking(self, text: str) -> None:
        """Update the thinking panel (replace)."""
        if not self.started or self.fallback_mode:
            return
        self.thinking_text = text
        self._flush_thinking_panel()

    def set_footer(self, text: str) -> None:
        """Update the footer (token usage, status)."""
        if not self.started or self.fallback_mode:
            return
        self.footer_text = text
        self._cardkit_content(EL_FOOTER, text)

    def set_header(self, title: str, template: str = "blue") -> bool:
        """Update the same card's lifecycle title without creating a message."""
        if not self.started or self.fallback_mode:
            return False
        try:
            req = (SettingsCardRequest.builder()
                   .card_id(self.card_id)
                   .request_body(
                       SettingsCardRequestBody.builder()
                       .settings(json.dumps({
                           "header": {
                               "title": {"tag": "plain_text", "content": title},
                               "template": template,
                           },
                           "summary": {"content": title},
                       }, ensure_ascii=False))
                       .sequence(self._next_seq())
                       .build()
                   )
                   .build())
            resp = self.client.cardkit.v1.card.settings(req)
            if not resp.success():
                print(f"[CardKit] settings(header) failed: {resp.code} {resp.msg}", flush=True)
                return False
            self.title = title
            return True
        except Exception as e:
            print(f"[CardKit] settings(header) exception: {e}", flush=True)
            return False

    def handoff_from_queue(self, initial_footer: str = "") -> bool:
        """Close a waiting card and start execution on a fresh message.

        A queued card can be far above the newest conversation messages. It is
        kept as a waiting receipt while execution begins on a new card at the
        end of the chat. The waiting card is not counted as an execution shard.
        """
        if not self.started or self.fallback_mode or self._rotating:
            return False
        self._rotating = True
        old_card_id = self.card_id
        try:
            handoff_text = queue_handoff_text(self.streaming_text)
            # Best effort: the waiting card remains useful if one final write
            # is rejected by CardKit.
            self._cardkit_content(EL_STREAMING, optimize_markdown_for_feishu(handoff_text))
            self._cardkit_content(EL_FOOTER, "")
            try:
                req = (SettingsCardRequest.builder()
                       .card_id(old_card_id)
                       .request_body(
                           SettingsCardRequestBody.builder()
                           .settings(json.dumps({
                               "config": {"streaming_mode": False},
                               "header": {
                                   "title": {"tag": "plain_text",
                                              "content": f"{self.title} · 已开始执行"},
                                   "template": "grey",
                               },
                               "summary": {"content": strip_markdown(handoff_text)},
                           }, ensure_ascii=False))
                           .sequence(self._next_seq())
                           .build())
                       .build())
                resp = self.client.cardkit.v1.card.settings(req)
                if not resp.success():
                    print(f"[CardKit] queue handoff close failed: {resp.code} {resp.msg}", flush=True)
            except Exception as exc:
                print(f"[CardKit] queue handoff close exception: {exc}", flush=True)

            new_card = build_initial_card(
                self.title, chat_id=self.chat_id, task_id=self.task_id,
                engine_key=self.engine_key if hasattr(self, "engine_key") else "claude",
            )
            try:
                req = (CreateCardRequest.builder()
                       .request_body(
                           CreateCardRequestBody.builder()
                           .type("card_json")
                           .data(json.dumps(new_card, ensure_ascii=False))
                           .build())
                       .build())
                resp = self.client.cardkit.v1.card.create(req)
                if not resp.success() or not resp.data or not resp.data.card_id:
                    print(f"[CardKit] queue handoff create failed: {resp.code} {resp.msg}", flush=True)
                    self.fallback_mode = True
                    return False
                new_card_id = resp.data.card_id
            except Exception as exc:
                print(f"[CardKit] queue handoff create exception: {exc}", flush=True)
                self.fallback_mode = True
                return False

            try:
                resp = self._send_card_message(new_card_id)
                if not resp.success() or not resp.data or not resp.data.message_id:
                    print(f"[CardKit] queue handoff send failed: {resp.code} {resp.msg}", flush=True)
                    self.fallback_mode = True
                    return False
                new_message_id = resp.data.message_id
            except Exception as exc:
                print(f"[CardKit] queue handoff send exception: {exc}", flush=True)
                self.fallback_mode = True
                return False

            # Commit the new execution shard only after its message exists.
            self.card_id = new_card_id
            self.message_id = new_message_id
            self.sequence = 0
            now = time.time()
            self._task_start_time = now
            self._card_start_time = now
            self._card_generation = 0
            self._streaming_expired = False
            self._last_panel_flush = 0
            self.streaming_text = "任务已开始，正在准备..."
            self.footer_text = ""
            self._flush_tool_panel()
            self._flush_thinking_panel()
            self._cardkit_content(EL_STREAMING, self.streaming_text)
            self._cardkit_content(EL_FOOTER, initial_footer or self._default_running_footer())
            return True
        except Exception as exc:
            print(f"[CardKit] queue handoff exception: {exc}", flush=True)
            self.fallback_mode = True
            return False
        finally:
            self._rotating = False

    # ===== Phase 3: Finalize =====

    def finalize(self, final_text: str, footer: str = "",
                 tool_header: str = "", status: str = "success") -> None:
        """Close streaming mode and set the final content.

        If `final_text` exceeds CARD_SHARD_THRESHOLD, the first shard is placed
        in the main (streaming) card and subsequent shards are sent as separate,
        visually distinct continuation cards (see `build_continuation_card`).
        """
        if not self.started or self.fallback_mode:
            return

        self._last_panel_flush = 0
        self._flush_tool_panel()
        self._flush_thinking_panel()

        shards = split_for_shards(final_text)
        total = len(shards)

        # First shard goes into the main card's streaming markdown element.
        first = shards[0]
        if total > 1:
            first = first + f"\n\n---\n*⋯ 接下文 (1/{total})*"
        self._cardkit_content(EL_STREAMING, optimize_markdown_for_feishu(first))

        if footer:
            self._cardkit_content(EL_FOOTER, footer)

        status_style = {
            "success": ("green", "完成"),
            "failed": ("red", "失败"),
            "timeout": ("orange", "超时"),
            "cancelled": ("grey", "已中止"),
        }
        template, outcome = status_style.get(status, status_style["failed"])
        base_title = re.sub(r"\s+working(?=\s*·|$)", "", self.title, flags=re.I)
        final_title = f"{base_title} · {outcome}"

        # Close streaming mode via Settings API. Button now lives in body[0]
        # (column_set), not header.extra, so Settings full-replace on header
        # no longer wipes it.
        try:
            req = (SettingsCardRequest.builder()
                   .card_id(self.card_id)
                   .request_body(
                       SettingsCardRequestBody.builder()
                       .settings(json.dumps({
                           "config": {"streaming_mode": False},
                            "header": {
                                "title": {"tag": "plain_text", "content": final_title},
                                "template": template,
                           },
                           "summary": {"content": strip_markdown(final_text)},
                       }, ensure_ascii=False))
                       .sequence(self._next_seq())
                       .build()
                   )
                   .build())
            resp = self.client.cardkit.v1.card.settings(req)
            if not resp.success():
                print(f"[CardKit] settings(close) failed: {resp.code} {resp.msg}", flush=True)
        except Exception as e:
            print(f"[CardKit] settings exception: {e}", flush=True)

        # Continuation cards for shards 2..N
        for i, shard in enumerate(shards[1:], start=2):
            self._send_continuation_card(i, total, shard)

    def _send_continuation_card(self, shard_idx: int, total: int, content: str) -> None:
        """Create and send one continuation card (non-streaming, wathet header)."""
        card = build_continuation_card(shard_idx, total, content)
        try:
            req = (CreateCardRequest.builder()
                   .request_body(
                       CreateCardRequestBody.builder()
                       .type("card_json")
                       .data(json.dumps(card, ensure_ascii=False))
                       .build()
                   )
                   .build())
            resp = self.client.cardkit.v1.card.create(req)
            if not resp.success() or not resp.data or not resp.data.card_id:
                print(f"[CardKit] cont {shard_idx}/{total} create failed: "
                      f"{resp.code} {resp.msg}", flush=True)
                return
            cont_card_id = resp.data.card_id
        except Exception as e:
            print(f"[CardKit] cont {shard_idx}/{total} create exception: {e}", flush=True)
            return

        try:
            card_ref = {"type": "card", "data": {"card_id": cont_card_id}}
            body = (CreateMessageRequestBody.builder()
                    .receive_id(self.chat_id)
                    .msg_type("interactive")
                    .content(json.dumps(card_ref, ensure_ascii=False))
                    .build())
            msg_req = (CreateMessageRequest.builder()
                       .receive_id_type("chat_id")
                       .request_body(body)
                       .build())
            resp2 = self.client.im.v1.message.create(msg_req)
            if not resp2.success():
                print(f"[CardKit] cont {shard_idx}/{total} send failed: "
                      f"{resp2.code} {resp2.msg}", flush=True)
        except Exception as e:
            print(f"[CardKit] cont {shard_idx}/{total} send exception: {e}", flush=True)

    # ===== Card rotation (bypass 10-minute streaming timeout) =====

    def _needs_rotation(self) -> bool:
        """Check if the current card is approaching the 10-min Feishu limit."""
        if not self.started or self.fallback_mode or self._rotating:
            return False
        if self._card_start_time == 0:
            return False
        if time.time() < self._rotation_retry_at:
            return False
        # Trigger on time (proactive) or on streaming expired error (reactive)
        if self._streaming_expired:
            return True
        return (time.time() - self._card_start_time) >= CARD_ROTATE_SECONDS

    def rotate_card(self, footer_text: str = "",
                    initial_footer: Optional[Any] = None) -> bool:
        """Finalize the current card and create a new one to continue streaming.

        ``footer_text`` is frozen onto the old card before it is closed.
        ``initial_footer`` may be a string or a zero-argument callable; it is
        evaluated only after the new card message is sent, so the new footer
        reports the new shard's duration and number.

        Returns True if rotation succeeded.  The generation is committed only
        after the new-card message succeeds. Expired old cards use ordinary
        element updates; their streaming endpoint cannot be used again.
        """
        if not self.started or self.fallback_mode or self._rotating:
            return False
        self._rotating = True
        self._rotation_retry_at = time.time() + 30
        gen = self._card_generation + 1
        try:
            print(f"[CardKit] Rotating card (gen {self._card_generation} -> {gen})", flush=True)

            # 1. Freeze the old card with its terminal text and footer.  Keep
            # the duration/continuation markers here even for direct callers
            # that provide only a short footer string.
            continuation_notice = "\n\n---\n*⬇ 任务持续中，后续输出在新卡片*"
            old_text = self.streaming_text or ""
            text_frozen = self._cardkit_content(
                EL_STREAMING,
                optimize_markdown_for_feishu(old_text + continuation_notice),
            )
            frozen_footer = (footer_text or "").strip()
            if "本分片耗时" not in frozen_footer:
                frozen_footer = (
                    f"{frozen_footer}\n" if frozen_footer else ""
                ) + f"本分片耗时 {format_duration(self.current_shard_elapsed_seconds())}"
            if "续接分片" not in frozen_footer:
                frozen_footer += f"\n↪ 续接分片 {gen + 1}"
            footer_frozen = self._cardkit_content(EL_FOOTER, frozen_footer)
            if (not text_frozen or not footer_frozen) and not self._streaming_expired:
                print("[CardKit] rotate: failed to freeze old card", flush=True)
                return False
            self.footer_text = frozen_footer

            # 2. Close streaming mode on old card.  A failed close is a failed
            # rotation: do not create a new card or claim the old one closed.
            try:
                old_title = self.title
                old_title = f"{old_title} · 分片 {self.shard_number} · 已续接"
                req = (SettingsCardRequest.builder()
                       .card_id(self.card_id)
                       .request_body(
                           SettingsCardRequestBody.builder()
                           .settings(json.dumps({
                               "config": {"streaming_mode": False},
                               "header": {
                                   "title": {"tag": "plain_text", "content": old_title},
                                   "template": "blue",
                               },
                           }, ensure_ascii=False))
                           .sequence(self._next_seq())
                           .build()
                       )
                       .build())
                resp = self.client.cardkit.v1.card.settings(req)
                if not resp.success() and resp.code not in (200850, 300309):
                    print(f"[CardKit] rotate: close old card failed: {resp.code} {resp.msg}", flush=True)
                    return False
                # If create/send fails, retries must use the non-streaming API
                # on this already-closed old card.
                self._streaming_expired = True
            except Exception as e:
                print(f"[CardKit] rotate: close old card exception: {e}", flush=True)
                return False

            # 3. Create new card
            new_title = f"{self.title} (续 {gen})"
            new_card = build_initial_card(
                new_title, chat_id=self.chat_id, task_id=self.task_id,
                engine_key=self.engine_key if hasattr(self, "engine_key") else "claude",
            )
            try:
                req = (CreateCardRequest.builder()
                       .request_body(
                           CreateCardRequestBody.builder()
                           .type("card_json")
                           .data(json.dumps(new_card, ensure_ascii=False))
                           .build()
                       )
                       .build())
                resp = self.client.cardkit.v1.card.create(req)
                if not resp.success() or not resp.data or not resp.data.card_id:
                    print(f"[CardKit] rotate: create new card failed: {resp.code} {resp.msg}", flush=True)
                    return False
                new_card_id = resp.data.card_id
            except Exception as e:
                print(f"[CardKit] rotate: create new card exception: {e}", flush=True)
                return False

            # 4. Send new card as message
            try:
                resp = self._send_card_message(new_card_id)
                if not resp.success() or not resp.data or not resp.data.message_id:
                    print(f"[CardKit] rotate: send new card msg failed: {resp.code} {resp.msg}", flush=True)
                    return False
                new_message_id = resp.data.message_id
            except Exception as e:
                print(f"[CardKit] rotate: send new card msg exception: {e}", flush=True)
                return False

            # 5. Swap state — reset for the new card.  Do this only after the
            # message exists so a failed rotation cannot consume a shard number.
            self.card_id = new_card_id
            self.message_id = new_message_id
            self.sequence = 0
            self._card_start_time = time.time()
            self._card_generation = gen
            self._streaming_expired = False
            self._rotation_retry_at = 0.0
            self._last_panel_flush = 0
            self._flush_tool_panel()
            self._flush_thinking_panel()
            if self.streaming_text:
                self._cardkit_content(EL_STREAMING, optimize_markdown_for_feishu(self.streaming_text))
            if callable(initial_footer):
                initial_footer = initial_footer()
            if initial_footer is None:
                initial_footer = self._default_running_footer()
            if initial_footer:
                self.footer_text = str(initial_footer)
                # The new card is visible now; do not wait for the next event
                # before showing that it is running.
                self._cardkit_content(EL_FOOTER, self.footer_text)
            print(f"[CardKit] Rotation complete, new card_id={new_card_id[:12]}...", flush=True)
            return True

        except Exception as e:
            print(f"[CardKit] rotate: unexpected error: {e}", flush=True)
            return False
        finally:
            self._rotating = False

    # ===== Low-level CardKit calls =====

    def _cardkit_content(self, element_id: str, content: str) -> bool:
        """Send cumulative content to a markdown element (CardKit typewriter)."""
        if not self.card_id:
            return False
        body_text = content if content else " "
        if self._streaming_expired:
            return self._update_element(element_id, {
                "tag": "markdown", "element_id": element_id, "content": body_text,
            })
        seq = self._next_seq()
        try:
            req = (ContentCardElementRequest.builder()
                   .card_id(self.card_id)
                   .element_id(element_id)
                   .request_body(
                       ContentCardElementRequestBody.builder()
                       .content(body_text)
                       .sequence(seq)
                       .build()
                   )
                   .build())
            t0 = time.time()
            resp = self.client.cardkit.v1.card_element.content(req)
            rtt_ms = int((time.time() - t0) * 1000)
            if not resp.success():
                code = resp.code
                print(f"[CardKit] content({element_id}) failed: {code} {resp.msg}", flush=True)
                if PERF_LOG:
                    print(f"[CardKit-perf] content({element_id}) {rtt_ms}ms len={len(body_text)} seq={seq} FAIL:{code}", flush=True)
                # 200850 = streaming timeout, 300309 = streaming already closed
                # These indicate the 10-min limit was hit; mark for emergency rotation
                if code in (200850, 300309):
                    self._streaming_expired = True
                    return self._update_element(element_id, {
                        "tag": "markdown", "element_id": element_id, "content": body_text,
                    })
                return False
            if PERF_LOG:
                print(f"[CardKit-perf] content({element_id}) {rtt_ms}ms len={len(body_text)} seq={seq} ok", flush=True)
            return True
        except Exception as e:
            print(f"[CardKit] content exception: {e}", flush=True)
            return False

    def _update_element(self, element_id: str, element_json: Dict[str, Any]) -> bool:
        """Replace an element's full JSON (for panels with nested children)."""
        if not self.card_id:
            return False
        body_text = json.dumps(element_json, ensure_ascii=False)
        seq = self._next_seq()
        try:
            req = (UpdateCardElementRequest.builder()
                   .card_id(self.card_id)
                   .element_id(element_id)
                   .request_body(
                       UpdateCardElementRequestBody.builder()
                       .element(body_text)
                       .sequence(seq)
                       .build()
                   )
                   .build())
            t0 = time.time()
            resp = self.client.cardkit.v1.card_element.update(req)
            rtt_ms = int((time.time() - t0) * 1000)
            if not resp.success():
                code = resp.code
                print(f"[CardKit] update({element_id}) failed: {code} {resp.msg}", flush=True)
                if PERF_LOG:
                    print(f"[CardKit-perf] update({element_id}) {rtt_ms}ms len={len(body_text)} seq={seq} FAIL:{code}", flush=True)
                if code in (200850, 300309):
                    self._streaming_expired = True
                return False
            if PERF_LOG:
                print(f"[CardKit-perf] update({element_id}) {rtt_ms}ms len={len(body_text)} seq={seq} ok", flush=True)
            return True
        except Exception as e:
            print(f"[CardKit] update exception: {e}", flush=True)
            return False

    def _flush_tool_panel(self) -> None:
        """Rebuild and push the tool panel with current steps."""
        now = time.time()
        if (now - self._last_panel_flush) * 1000 < CARDKIT_MS * 3:
            return
        self._last_panel_flush = now

        n = len(self.tool_steps)
        header_title = f"🛠️ Tool use · 累计 {self.tool_total} 次 · 最近 {n} 条"
        panel = {
            "tag": "collapsible_panel",
            "element_id": EL_TOOL_PANEL,
            "expanded": True,
            "header": {
                "title": {
                    "tag": "plain_text",
                    "content": header_title,
                    "text_color": "grey",
                    "text_size": "notation",
                },
                "icon": {
                    "tag": "standard_icon",
                    "token": "down-small-ccm_outlined",
                    "size": "14px 14px",
                },
            },
            "border": {"color": "grey", "corner_radius": "5px"},
            "elements": self.tool_steps,
        }
        self._update_element(EL_TOOL_PANEL, panel)

    def add_public_progress(self, text, key=""):
        text = str(text or "").strip()
        signature = sha256(text.encode("utf-8")).hexdigest()
        if not text or self._progress_seen.get(key or "latest") == signature:
            return False
        self._progress_seen[key or "latest"] = signature
        stamp = datetime.now(timezone(timedelta(hours=8))).strftime("%H:%M:%S")
        self.progress_entries.append(f"**{stamp}**\n{text[:1200]}")
        self.progress_total += 1
        while len(self.progress_entries) > 12 or sum(map(len, self.progress_entries)) > 8000:
            self.progress_entries.pop(0)
        self._flush_thinking_panel()
        return True

    def record_tool(self, key, action, completed=False, failed=False):
        if key not in self._tool_seen:
            self._tool_seen.add(key)
            self.tool_total += 1
        if action:
            self._tool_actions[key] = str(action)[:180]
        action = self._tool_actions.get(key, "工具调用")
        state = "失败" if failed else ("已完成" if completed else "执行中")
        self._tool_rows[key] = build_tool_step_element(f"{action} · {state}")
        if key in self._recent_tool_ids:
            self._recent_tool_ids.remove(key)
        self._recent_tool_ids.append(key)
        self._recent_tool_ids = self._recent_tool_ids[-8:]
        self.tool_steps = [self._tool_rows[k] for k in self._recent_tool_ids]
        self._tool_rows = {k: self._tool_rows[k] for k in self._recent_tool_ids}
        self._flush_tool_panel()
        return self.tool_total

    def _public_panel_text(self):
        parts = list(self.progress_entries)
        if self.thinking_text:
            parts.append(self.thinking_text[-8000:])
        return "\n\n".join(parts) or " "

    def _flush_thinking_panel(self) -> None:
        """Push thinking text into the thinking panel."""
        panel = {
            "tag": "collapsible_panel",
            "element_id": EL_THINKING_PANEL,
            "expanded": False,
            "header": {
                "title": {
                    "tag": "plain_text",
                    "content": f"💭 Thought · 关键进展 {self.progress_total} 条" if self.progress_entries else "💭 Thought",
                    "text_color": "grey",
                    "text_size": "notation",
                },
                "icon": {
                    "tag": "standard_icon",
                    "token": "down-small-ccm_outlined",
                    "size": "14px 14px",
                },
            },
            "border": {"color": "grey", "corner_radius": "5px"},
            "elements": [
                {
                    "tag": "markdown",
                    "content": self._public_panel_text(),
                    "text_size": "notation",
                }
            ],
        }
        self._update_element(EL_THINKING_PANEL, panel)


# ===== CardKitReporter — status_cb adapter =====

class CardKitReporter:
    """Adapter that consumes status_cb events from ClaudeRunner and drives a
    CardKitStreamer. Falls back to a legacy StatusReporter if CardKit fails.

    The fallback class must be supplied via `legacy_factory` (avoids a circular
    import with main.py).
    """

    def __init__(self, client: lark.Client, chat_id: str,
                 title: str = "Claude 工作中",
                 task_id: str = "",
                 reply_to: str = "",
                 reply_in_thread: bool = False,
                 legacy_factory: Optional[Callable[..., Any]] = None) -> None:
        self.client: lark.Client = client
        self.chat_id: str = chat_id
        self.title: str = title
        self.task_id: str = task_id
        self.legacy_factory: Optional[Callable[..., Any]] = legacy_factory

        self.streamer: CardKitStreamer = CardKitStreamer(
            client, chat_id, title, task_id=task_id,
            reply_to=reply_to, reply_in_thread=reply_in_thread,
        )
        self.legacy: Optional[Any] = None  # lazy, created on fallback

        # State tracking
        self.start_time: float = time.time()
        self._last_actual_event = time.monotonic()
        self._last_public_action = "等待引擎事件"
        self._terminal_status = "success"
        self.latest_step: int = 0
        self.latest_elapsed: int = 0
        self.usage: Dict[str, int] = {"in": 0, "out": 0, "cache_r": 0, "cache_c": 0}
        self.cost_usd: float = 0.0
        self.task_usage_event: Optional[Dict[str, Any]] = None
        self.task_usage_progress: Optional[Dict[str, Any]] = None

        # Stream throttle
        self._last_text_flush: float = 0.0
        self._last_footer_flush: float = 0.0
        self._last_thinking_flush: float = 0.0
        self._lock = threading.RLock()
        self._heartbeat_stop = threading.Event()
        self._heartbeat_thread = None
        self._finalized = False
        self._queued = False
        self._execution_started = False
        self._last_rotation_notice = 0.0

        # Track whether the last tool step is a placeholder awaiting the real
        # assistant event — set by tool_start, cleared by tool.
        self._last_step_placeholder: bool = False

    @_serialized_render
    def start(self) -> None:
        """Create the card. If CardKit fails, fall back to legacy reporter."""
        initial_footer = self._footer(done=False)
        try:
            # The reporter owns the richer step/usage footer.  Suppress the
            # Streamer's generic footer here so exactly one footer write occurs
            # immediately after the message is confirmed sent.
            ok = self.streamer.start(initial_footer="")
        except TypeError as exc:
            # Keep compatibility with injected/older Streamer adapters that
            # still expose the original no-argument start().
            if "initial_footer" not in str(exc):
                raise
            ok = self.streamer.start()
        if ok:
            self.streamer.set_footer(initial_footer)
        if not ok and self.legacy_factory:
            print("[CardKitReporter] falling back to legacy StatusReporter", flush=True)
            try:
                self.legacy = self.legacy_factory(
                    self.chat_id, self.title,
                    reply_to=self.streamer.reply_to,
                    reply_in_thread=self.streamer.reply_in_thread,
                )
            except TypeError:
                self.legacy = self.legacy_factory(self.chat_id, self.title)
            self.legacy.start("接收请求...")
        if ok and self._heartbeat_thread is None:
            self._heartbeat_thread = threading.Thread(
                target=self._heartbeat_loop, name="cardkit-heartbeat", daemon=True,
            )
            self._heartbeat_thread.start()

    @_serialized_render
    def mark_queued(self, position: int, preview: str = "") -> None:
        """Render a waiting task on the card that will later execute it."""
        # Queue-position callbacks can race with dequeue. Once execution has
        # handed off to a fresh card, stale callbacks must not put it back into
        # the waiting state.
        if self._execution_started:
            return
        self._queued = True
        note = (
            "已进入调度队列，即将开始。"
            if position <= 0
            else f"排队中，前面还有 {position} 个任务，轮到后自动开始。"
        )
        body = note
        if preview:
            body += f"\n\n> 任务：{preview}"
        if self.legacy:
            self.legacy.title = self.title
            self.legacy.current_stream_text = body
            self.legacy.current_stream_elapsed = 0
            self.legacy._dirty.set()
            return
        self.streamer.set_header(self.title, template="grey")
        self.streamer.set_streaming_text(body)
        self.streamer.set_footer("")

    @_serialized_render
    def begin_running(self, title: str) -> None:
        """Transition a pre-created queued card into its running phase."""
        if self._finalized or self._execution_started:
            return
        was_queued = self._queued
        self.title = title
        self.start_time = time.time()
        self.latest_step = 0
        self.latest_elapsed = 0
        self.usage = {"in": 0, "out": 0, "cache_r": 0, "cache_c": 0}
        self.cost_usd = 0.0
        self.task_usage_event = None
        self.task_usage_progress = None
        self._queued = False
        self._execution_started = True
        self._last_actual_event = time.monotonic()
        self.streamer.title = title
        if was_queued and not self.legacy:
            initial_footer = self._footer(done=False)
            if self.streamer.handoff_from_queue(initial_footer=initial_footer):
                return
            # CardKit failed after the queue receipt was created. Continue via
            # a new legacy message rather than silently reusing the old card.
            self._start_legacy_execution(title)
            return
        if was_queued and self.legacy:
            self._start_legacy_execution(title)
            return
        if not self.legacy:
            reset_clock = getattr(self.streamer, "reset_task_clock", None)
            if callable(reset_clock):
                reset_clock(self.start_time)
        if self.legacy:
            self.legacy.title = title
            self.legacy.current_stream_text = "任务已开始，正在准备..."
            self.legacy.current_stream_elapsed = 0
            self.legacy._dirty.set()
            return
        self._rotate_if_needed()
        self.streamer.set_header(title, template="blue")
        self.streamer.streaming_text = "任务已开始，正在准备..."
        self.streamer._cardkit_content(
            EL_STREAMING, "任务已开始，正在准备..."
        )
        self.streamer.set_footer(self._footer(done=False))

    def _start_legacy_execution(self, title: str) -> None:
        """Create a new fallback message for a queued task handoff."""
        old = self.legacy
        if old is not None:
            handoff = getattr(old, "handoff", None)
            if callable(handoff):
                handoff()
            elif callable(getattr(old, "stop", None)):
                old.stop()
        if not self.legacy_factory:
            return
        try:
            self.legacy = self.legacy_factory(
                self.chat_id, title,
                reply_to=self.streamer.reply_to,
                reply_in_thread=self.streamer.reply_in_thread,
            )
        except TypeError:
            self.legacy = self.legacy_factory(self.chat_id, title)
        self.legacy.start("任务已开始，正在准备...")

    def _fmt_usage(self) -> str:
        if self.task_usage_event is not None:
            from token_usage import format_task_usage
            # Once a terminal usage event exists, show only its authoritative
            # task result. A live estimate must not survive beside an
            # incomplete final line and look like a second answer.
            return format_task_usage(self.task_usage_event)
        if self.task_usage_progress is not None:
            from token_usage import format_task_usage_progress
            return format_task_usage_progress(self.task_usage_progress)
        u: Dict[str, int] = self.usage
        parts: List[str] = []
        if u["in"]:
            parts.append(f"in {u['in']:,}")
        if u["out"]:
            parts.append(f"out {u['out']:,}")
        if u["cache_r"]:
            parts.append(f"cache↓{u['cache_r']:,}")
        if u["cache_c"]:
            parts.append(f"cache↑{u['cache_c']:,}")
        line = " · ".join(parts)
        if self.cost_usd:
            line += f" · ${self.cost_usd:.4f}"
        return f"当前调用：{line}" if line else ""

    @staticmethod
    def _number(value: Any, default: float = 0.0) -> float:
        if isinstance(value, bool) or not isinstance(value, Real):
            return default
        try:
            value = float(value)
        except (TypeError, ValueError, OverflowError):
            return default
        return max(0.0, value)

    def _streamer_seconds(self, method_name: str, now: float) -> float:
        try:
            method = getattr(self.streamer, method_name)
            value = method(now)
        except (AttributeError, TypeError, ValueError):
            return 0.0
        return self._number(value)

    def _shard_number(self) -> int:
        try:
            value = getattr(self.streamer, "shard_number")
            if callable(value):
                value = value()
            value = int(value)
        except (AttributeError, TypeError, ValueError, OverflowError):
            return 1
        return max(1, value)

    def _footer(self, done: bool = False, status: str = "success",
                continuation: bool = False) -> str:
        outcomes = {
            "success": ("✅", "完成"),
            "failed": ("❌", "失败"),
            "timeout": ("⏱️", "超时"),
            "cancelled": ("⏹️", "已中止"),
        }
        now = time.time()
        event_elapsed = self._number(self.latest_elapsed)
        wall_elapsed = self._number(now - self.start_time)
        streamer_task_elapsed = self._streamer_seconds("task_elapsed_seconds", now)
        task_elapsed = max(event_elapsed, wall_elapsed, streamer_task_elapsed)
        shard_number = self._shard_number()
        shard_elapsed = self._streamer_seconds("current_shard_elapsed_seconds", now)

        if continuation:
            head = (
                f"↪ 分片 {shard_number} 已结束 · "
                f"本分片耗时 {format_duration(shard_elapsed)} · "
                f"续接分片 {shard_number + 1}"
            )
            detail = (
                f"任务累计时长 {format_duration(task_elapsed)} · "
                f"{self.latest_step} 步"
            )
            head = f"{head}\n{detail}"
        else:
            icon, label = outcomes.get(status, outcomes["failed"]) if done else ("⋯", "运行中")
            head = (
                f"{icon} {label} · 任务累计时长 {format_duration(task_elapsed)}"
                f" · 当前卡片时长 {format_duration(shard_elapsed)}"
                f" · 分片 {shard_number} · {self.latest_step} 步"
            )
        if done:
            head = f"{head} · {time.strftime('%Y-%m-%d %H:%M', time.localtime())}"
        if not done:
            quiet = max(0, int(time.monotonic() - self._last_actual_event))
            # The footer is reserved for lifecycle, duration and usage.
            # Public phase/tool text belongs in the progress/tool panels; a
            # reasoning wait status here reads like a final result and pushes
            # transient process text into the card's bottom line.
            head += f"\n上次引擎事件 {quiet}s 前"
        usage = self._fmt_usage()
        return f"{head}\n{usage}" if usage else head

    @_serialized_render
    def on_event(self, evt: Dict[str, Any]) -> None:
        """Receive a status_cb event from ClaudeRunner."""
        if self._finalized:
            return
        if evt.get("type") not in ("usage", "usage_final", "done"):
            self._last_actual_event = time.monotonic()
        if evt.get("type") == "done":
            self._terminal_status = evt.get("status", self._terminal_status)
        if self._consume_public_event(evt):
            return
        if evt.get("type") == "tool":
            self.streamer.tool_total = max(self.streamer.tool_total, int(evt.get("step", 0) or 0))
        if self.legacy:
            self.legacy.on_event(evt)
            return

        etype: Optional[str] = evt.get("type")

        self._rotate_if_needed()

        with self._lock:
            self._render_event(evt, etype)

    def _rotate_if_needed(self) -> None:
        """Also called by the timer when the engine emits no events."""
        if self.streamer._needs_rotation():
            footer = self._footer(continuation=True)
            try:
                ok = self.streamer.rotate_card(
                    footer_text=footer,
                    # Reporter writes the task-aware footer after the new
                    # message is confirmed; suppress the generic one.
                    initial_footer="",
                )
            except TypeError as exc:
                # The fallback keeps test/injected streamers with the
                # pre-optimization rotate_card(footer_text) signature usable.
                if "initial_footer" not in str(exc):
                    raise
                ok = self.streamer.rotate_card(footer_text=footer)
            if ok:
                self.streamer.set_footer(self._footer(done=False))
            if not ok:
                now = time.time()
                if now - self._last_rotation_notice >= 120:
                    self._last_rotation_notice = now
                    self._send_heartbeat("⚠️ 流式渲染续卡失败，任务仍在运行")

    def _render_event(self, evt: Dict[str, Any], etype: Optional[str]) -> None:
        with self._lock:
            if etype == "tool_start":
                # Placeholder step emitted on content_block_start(tool_use).
                # Shows activity during the 5-30s gap before the full assistant
                # event arrives (which has complete tool_input args).
                self.latest_elapsed = evt.get("elapsed", self.latest_elapsed)
                name = evt.get("tool_name") or "?"
                icon = TOOL_ICONS.get(name, "setting_outlined")
                self.streamer.add_tool_step(f"{name} · 准备中...", icon=icon)
                self._last_step_placeholder = True
                # Tool activity is rendered in the Tool use panel; it does not
                # justify rewriting the footer on every event.

            elif etype == "tool":
                self.latest_step = max(self.latest_step, int(evt.get("step", self.latest_step)))
                self.latest_elapsed = evt.get("elapsed", self.latest_elapsed)
                name = evt.get("tool_name") or "?"
                inp = evt.get("tool_input") or {}
                action = format_tool_action_human(name, inp)
                icon = TOOL_ICONS.get(name, "setting_outlined")
                if self._last_step_placeholder and self.streamer.tool_steps:
                    # Replace placeholder in place, no extra step.
                    self.streamer.tool_steps[-1] = build_tool_step_element(action, icon)
                    self.streamer._flush_tool_panel()
                    self._last_step_placeholder = False
                else:
                    self.streamer.add_tool_step(action, icon=icon)
                # Keep the small footer stable between shard/terminal boundaries.

            elif etype == "tool_result":
                self.latest_elapsed = evt.get("elapsed", self.latest_elapsed)
                # For now, tool results are not inlined into the card (keeps it compact).
                # Could be added as a sub-element of the tool panel later.

            elif etype == "partial_text":
                text = evt.get("text", "")
                self.latest_elapsed = evt.get("elapsed", self.latest_elapsed)
                # Always retain the newest body, including deltas coalesced by
                # the network throttle. A completed message must pin its last
                # update before tools or a quiet period can leave it stale.
                self.streamer.streaming_text = text
                now = time.time()
                if (not evt.get("completed")
                        and (now - self._last_text_flush) * 1000 < CARDKIT_MS):
                    return
                self._last_text_flush = now
                self.streamer._cardkit_content(
                    EL_STREAMING, optimize_markdown_for_feishu(text)
                )

            elif etype == "thinking_stream":
                # Live thinking_delta accumulation (throttled upstream at 250ms).
                # text is always the full cumulative thinking across all blocks
                # of the current turn, so we replace rather than append.
                self.latest_elapsed = evt.get("elapsed", self.latest_elapsed)
                text = evt.get("text", "")
                now = time.time()
                if (now - self._last_thinking_flush) * 1000 < CARDKIT_MS:
                    return
                self._last_thinking_flush = now
                self.streamer.thinking_text = text
                self.streamer._flush_thinking_panel()

            elif etype == "thinking":
                # Final authoritative thinking text from assistant event.
                # Already covered by thinking_stream during streaming — just
                # ensure the final state is pinned.
                self.latest_elapsed = evt.get("elapsed", self.latest_elapsed)
                text = evt.get("text", "")
                if text and text not in (self.streamer.thinking_text or ""):
                    prev = self.streamer.thinking_text
                    self.streamer.thinking_text = (
                        prev + "\n\n" + text if prev else text
                    )
                    self.streamer._flush_thinking_panel()

            elif etype == "usage_progress":
                self.task_usage_progress = dict(evt)
                self.usage["in"] = evt.get("input_tokens", self.usage["in"])
                self.usage["out"] = evt.get("output_tokens", self.usage["out"])
                self.usage["cache_r"] = evt.get("cache_read", self.usage["cache_r"])
                self.usage["cache_c"] = evt.get("cache_create", self.usage["cache_c"])
                # Codex can emit this turn-level estimate repeatedly while a
                # shard is running. Keep the latest value in memory; the
                # shard rotation or terminal usage event writes it once.

            elif etype in ("usage", "usage_final"):
                # Accept both the current task-scoped event and the original
                # Codex final event, which only carried top-level counts.
                if etype == "usage_final":
                    self.task_usage_event = dict(evt)
                if evt.get("usage_scope") == "task":
                    self.task_usage_event = dict(evt)
                self.usage["in"] = evt.get("input_tokens", self.usage["in"])
                self.usage["out"] = evt.get("output_tokens", self.usage["out"])
                self.usage["cache_r"] = evt.get("cache_read", self.usage["cache_r"])
                self.usage["cache_c"] = evt.get("cache_create", self.usage["cache_c"])
                if etype == "usage_final":
                    self.cost_usd = evt.get("total_cost_usd", self.cost_usd)
                # Usage is a shard/turn boundary. Write the small footer once
                # for this authoritative event; repeated usage_progress events
                # never reach CardKit.
                self.streamer.set_footer(self._footer(done=False))

            elif etype == "done":
                self.latest_step = max(self.latest_step, int(evt.get("step", self.latest_step)))
                self.latest_elapsed = evt.get("elapsed", self.latest_elapsed)
                self._heartbeat_stop.set()
                # Final finalize happens via finalize() call from the dispatcher.

    def _maybe_flush_footer(self) -> None:
        """Compatibility hook; event-level footer writes are disabled."""
        return

    @_serialized_render
    def _heartbeat_tick(self) -> None:
        if self._finalized or self._queued or self.legacy or self._heartbeat_stop.is_set():
            return
        if self.streamer.started and not self.streamer.fallback_mode:
            self.latest_elapsed = max(self.latest_elapsed, time.time() - self.start_time)
            # Heartbeats keep rotation and liveness local. Do not rewrite the
            # footer every 10 seconds just to advance a duration/quiet label.
            self._rotate_if_needed()

    def _heartbeat_loop(self) -> None:
        while not self._heartbeat_stop.wait(10):
            try:
                self._heartbeat_tick()
            except Exception as exc:
                print(f"[CardKit] heartbeat render exception: {exc}", flush=True)

    def _send_heartbeat(self, text: str) -> None:
        """Send a plain text message as heartbeat fallback when CardKit fails."""
        try:
            from lark_oapi.api.im.v1 import (
                CreateMessageRequest as _CMReq,
                CreateMessageRequestBody as _CMBody,
            )
            body = (_CMBody.builder()
                    .receive_id(self.chat_id)
                    .msg_type("text")
                    .content(json.dumps({"text": text}, ensure_ascii=False))
                    .build())
            req = (_CMReq.builder()
                   .receive_id_type("chat_id")
                   .request_body(body)
                   .build())
            resp = self.client.im.v1.message.create(req)
            if not resp.success():
                print(f"[CardKit] heartbeat send failed: {resp.code} {resp.msg}", flush=True)
        except Exception as e:
            print(f"[CardKit] heartbeat exception: {e}", flush=True)

    @_serialized_render
    def finalize(self, final_text: str, status: str = "success") -> None:
        """Replace card content with the full final answer and close streaming."""
        if self._finalized:
            return
        self._finalized = True
        self._record_public({"type": "terminal", "status": status})
        self._heartbeat_stop.set()
        if self.legacy:
            if hasattr(self.legacy, "finalize"):
                self.legacy.finalize(status)
            return

        footer = self._footer(done=True, status=status)
        self.streamer.finalize(final_text, footer=footer, status=status)


# ===== Tool formatting (simpler, more human than the old [Read] (L1-30) style) =====

    def _record_public(self, event):
        # Only public summaries and reduced tool metadata; never raw runner events.
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", str(self.task_id or "")):
            return
        try:
            state = Path(os.environ.get("AUTOBOT_STATE_DIR") or str(Path(__file__).resolve().parent / "data"))
            directory = state / "task_progress"
            directory.mkdir(parents=True, exist_ok=True, mode=0o700)
            event = {"at": datetime.now(timezone.utc).isoformat(), **event}
            descriptor = os.open(directory / (self.task_id + ".jsonl"), os.O_CREAT | os.O_APPEND | os.O_WRONLY, 0o600)
            try:
                os.write(descriptor, (json.dumps(event, ensure_ascii=False) + "\n").encode("utf-8"))
            finally:
                os.close(descriptor)
        except (OSError, ValueError, TypeError):
            # A journal outage must never interrupt work or cause task replay.
            pass

    def _consume_public_event(self, evt):
        kind = evt.get("type")
        if kind == "activity":
            return True
        if kind == "phase":
            phase = str(evt.get("text") or "引擎状态已更新").strip()[:180]
            self._last_public_action = phase
            # Reasoning lifecycle text is a public progress hint, not a tool
            # call and not footer metadata. Keep it with Thinking/Thoughts;
            # an explicit summary will be appended/replaced by thinking_stream.
            if evt.get("phase") == "reasoning" and phase:
                self.streamer.add_public_progress("⏳ " + phase, "phase:reasoning")
            return True
        if kind == "input_request":
            questions = evt.get("questions") or []
            prompts = [
                str(q.get("question") or q.get("header") or "").strip()
                for q in questions if isinstance(q, dict)
            ]
            prompts = [p for p in prompts if p]
            text = "📥 当前任务需要补充信息：" + "；".join(prompts[:3])
            if self.streamer.add_public_progress(text, "input_request"):
                self._last_public_action = text[:180]
                self._record_public({"type": "input_request", "text": text})
            return True
        if kind in ("progress", "process_message"):
            if evt.get("completed", True) is False:
                return True  # Keep partial commentary in the main text until complete.
            text = str(evt.get("text") or "").strip()
            key = str(evt.get("item_id") or evt.get("message_id") or "latest")
            if self.streamer.add_public_progress(text, key):
                self._last_public_action = text.splitlines()[0][:180]
                self._record_public({"type": "progress", "text": text})
            if self.legacy:
                self.legacy.on_event({"type": "thinking", "text": text, "elapsed": evt.get("elapsed", 0)})
            return True
        key = evt.get("tool_id") or evt.get("item_id")
        if kind in ("tool", "tool_result", "tool_progress") and key:
            completed = kind == "tool_result" or bool(evt.get("completed"))
            failed = bool(evt.get("is_error") or evt.get("failed"))
            action = str(evt.get("action") or evt.get("tool_name") or "")[:180]
            total = self.streamer.record_tool(str(key), action, completed, failed)
            self.latest_step = max(self.latest_step, total)
            self.latest_elapsed = evt.get("elapsed", self.latest_elapsed)
            self._last_public_action = self.streamer._tool_actions.get(str(key), "工具调用")
            if kind != "tool_progress":
                self._record_public({"type": "tool", "tool_id": str(key), "step": self.latest_step,
                                     "action": self._last_public_action, "completed": completed, "failed": failed})
            if self.legacy:
                self.legacy.on_event({**evt, "step": self.latest_step, "action": self._last_public_action})
            return True
        return False


def format_tool_action_human(name: str, inp: Dict[str, Any]) -> str:
    """Human-friendly tool action description for card display."""
    try:
        if name == "Read":
            fp: str = inp.get("file_path", "?")
            return f"正在读取 {os.path.basename(fp)}"
        if name == "Write":
            fp = inp.get("file_path", "?")
            return f"正在写入 {os.path.basename(fp)}"
        if name == "Edit":
            fp = inp.get("file_path", "?")
            return f"正在编辑 {os.path.basename(fp)}"
        if name == "Bash":
            desc: str = inp.get("description") or (inp.get("command", "")[:60])
            return f"执行: {desc}"
        if name == "Grep":
            pat: str = inp.get("pattern", "")
            return f"搜索 `{pat[:40]}`"
        if name == "Glob":
            pat = inp.get("pattern", "")
            return f"查找文件 `{pat[:40]}`"
        if name == "WebSearch":
            q: str = inp.get("query", "")
            return f"搜索网络: {q[:50]}"
        if name == "WebFetch":
            url: str = inp.get("url", "")
            return f"获取: {url[:60]}"
        if name == "Task":
            d: str = inp.get("description", "")
            return f"启动 Agent: {d[:50]}"
        if name == "TodoWrite":
            n: int = len(inp.get("todos", []) or [])
            return f"更新任务列表 ({n} 项)"
        return name
    except Exception:
        return name
