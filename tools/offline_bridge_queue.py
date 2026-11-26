"""Durable local queue for single-window bridge input.

The queue is intentionally local to the isolated Demo state directory.  It
stores only messages that still need to be delivered to the explicitly bound
CCPad session; bridge event telemetry continues to store hashes only.
"""
from __future__ import annotations

import hashlib
import json
import os
import tempfile
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


SCHEMA = 1
MAX_ITEMS = 500
MAX_TEXT_BYTES = 256 * 1024


class OfflineQueueError(RuntimeError):
    """Raised when a queue record cannot be read or written safely."""


class OfflineBridgeQueue:
    """Small JSON-backed FIFO with idempotent message keys and atomic writes."""

    def __init__(self, path: Path, *, max_items: int = MAX_ITEMS) -> None:
        self.path = path
        self.max_items = max(1, int(max_items))
        self._lock = threading.RLock()

    @staticmethod
    def _key(message_id: str) -> str:
        if not message_id:
            raise ValueError("message_id is required")
        return hashlib.sha256(message_id.encode("utf-8")).hexdigest()

    def _read(self) -> list[dict[str, Any]]:
        try:
            raw = self.path.read_text(encoding="utf-8")
        except FileNotFoundError:
            return []
        except OSError as exc:
            raise OfflineQueueError("cannot read offline queue") from exc
        try:
            body = json.loads(raw)
        except (TypeError, ValueError) as exc:
            raise OfflineQueueError("offline queue is not valid JSON") from exc
        if not isinstance(body, dict) or body.get("schema") != SCHEMA:
            raise OfflineQueueError("offline queue schema is unsupported")
        items = body.get("items")
        if not isinstance(items, list) or any(not isinstance(item, dict) for item in items):
            raise OfflineQueueError("offline queue items are invalid")
        return [dict(item) for item in items]

    def _write(self, items: list[dict[str, Any]]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = json.dumps({"schema": SCHEMA, "items": items},
                             ensure_ascii=False, indent=2) + "\n"
        temp_path: Path | None = None
        try:
            with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=self.path.parent,
                                             prefix=self.path.name + ".", suffix=".tmp",
                                             delete=False) as fp:
                temp_path = Path(fp.name)
                fp.write(payload)
                fp.flush()
                os.fsync(fp.fileno())
            os.replace(temp_path, self.path)
        except OSError as exc:
            try:
                if temp_path is not None:
                    temp_path.unlink(missing_ok=True)
            except OSError:
                pass
            raise OfflineQueueError("cannot write offline queue") from exc

    def pending(self) -> list[dict[str, Any]]:
        with self._lock:
            return self._read()

    def enqueue(self, message_id: str, chat_id: str, kind: str, *,
                text: str = "", payload: dict[str, str] | None = None) -> bool:
        """Append one input item; return false when the message is already queued."""
        if not chat_id:
            raise ValueError("chat_id is required")
        if kind not in {"text", "image", "file", "audio", "media", "video"}:
            raise ValueError("unsupported queue item kind")
        encoded = text.encode("utf-8")
        if len(encoded) > MAX_TEXT_BYTES:
            raise ValueError("queued text exceeds the local limit")
        key = self._key(message_id)
        with self._lock:
            items = self._read()
            if any(str(item.get("message_id_sha256") or "") == key for item in items):
                return False
            if len(items) >= self.max_items:
                raise OfflineQueueError("offline queue is full")
            now = datetime.now(timezone.utc).isoformat()
            item = {
                "message_id": message_id,
                "message_id_sha256": key,
                "chat_id": chat_id,
                "kind": kind,
                "text": text if kind == "text" else "",
                "payload": dict(payload or {}),
                "enqueued_at_utc": now,
                "attempts": 0,
                "last_error": "",
                "input_id": key[:32],
                "delivery_state": "pending",
            }
            self._write(items + [item])
            return True

    def begin_delivery(self, message_id: str) -> str:
        key = self._key(message_id)
        with self._lock:
            items = self._read()
            if not items or items[0].get("message_id_sha256") != key:
                raise OfflineQueueError("pending FIFO input must be resolved first")
            for item in items:
                if item.get("message_id_sha256") == key:
                    if not item.get("input_id") or item.get("delivery_state") == "delivery_unknown":
                        raise OfflineQueueError("input_delivery_unknown")
                    # Persist before sending; a crash must not replay uncertain input.
                    item["delivery_state"] = "delivery_unknown"
                    self._write(items)
                    return str(item["input_id"])
            raise OfflineQueueError("queued input missing")

    def mark_rejected(self, message_id: str) -> None:
        key = self._key(message_id)
        with self._lock:
            items = self._read()
            for item in items:
                if item.get("message_id_sha256") == key:
                    item["delivery_state"] = "pending"
            self._write(items)

    def ack(self, message_id: str, input_id: str = "") -> bool:
        key = self._key(message_id)
        with self._lock:
            items = self._read()
            if not input_id or not any(item.get("message_id_sha256") == key
                                       and item.get("input_id") == input_id for item in items):
                return False
            remaining = [item for item in items
                         if str(item.get("message_id_sha256") or "") != key]
            if len(remaining) == len(items):
                return False
            self._write(remaining)
            return True

    def mark_failed(self, message_id: str, error_type: str) -> bool:
        key = self._key(message_id)
        with self._lock:
            items = self._read()
            changed = False
            for item in items:
                if str(item.get("message_id_sha256") or "") == key:
                    item["attempts"] = int(item.get("attempts") or 0) + 1
                    item["last_error"] = str(error_type or "Error")[:120]
                    changed = True
                    break
            if changed:
                self._write(items)
            return changed
