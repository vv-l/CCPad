"""Local historical index of stable CCPad CLI sessions for the isolated Demo."""
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
MAX_SESSIONS = 200


class SessionIndexError(RuntimeError):
    """Raised when the local session index cannot be read or written safely."""


class SessionIndex:
    def __init__(self, path: Path, *, max_sessions: int = MAX_SESSIONS) -> None:
        self.path = path
        self.max_sessions = max(1, int(max_sessions))
        self._lock = threading.RLock()

    @staticmethod
    def _identity(session: dict[str, Any]) -> tuple[str, str, str]:
        return (
            str(session.get("sessionId") or ""),
            str(session.get("cliMode") or ""),
            str(session.get("workingDir") or ""),
        )

    @classmethod
    def _key(cls, session: dict[str, Any]) -> str:
        identity = "\x1f".join(cls._identity(session))
        if not identity.strip("\x1f"):
            raise ValueError("session has no stable identity")
        return hashlib.sha256(identity.encode("utf-8")).hexdigest()

    def _read(self) -> list[dict[str, Any]]:
        try:
            raw = self.path.read_text(encoding="utf-8")
        except FileNotFoundError:
            return []
        except OSError as exc:
            raise SessionIndexError("cannot read session index") from exc
        try:
            body = json.loads(raw)
        except (TypeError, ValueError) as exc:
            raise SessionIndexError("session index is not valid JSON") from exc
        if not isinstance(body, dict) or body.get("schema") != SCHEMA:
            raise SessionIndexError("session index schema is unsupported")
        sessions = body.get("sessions")
        if not isinstance(sessions, list) or any(not isinstance(item, dict) for item in sessions):
            raise SessionIndexError("session index entries are invalid")
        return [dict(item) for item in sessions]

    def _write(self, sessions: list[dict[str, Any]]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = json.dumps({"schema": SCHEMA, "sessions": sessions},
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
            raise SessionIndexError("cannot write session index") from exc

    def record(self, sessions: list[dict[str, Any]]) -> int:
        """Record visible sessions and return the number of stable entries."""
        now = datetime.now(timezone.utc).isoformat()
        with self._lock:
            existing = {str(item.get("identity_sha256") or ""): item
                        for item in self._read()}
            for session in sessions:
                try:
                    key = self._key(session)
                except ValueError:
                    continue
                previous = existing.get(key, {})
                existing[key] = {
                    "identity_sha256": key,
                    "cli_session_id": self._identity(session)[0],
                    "cli_mode": self._identity(session)[1],
                    "working_dir": self._identity(session)[2],
                    "label": str(session.get("label") or session.get("command") or "terminal"),
                    "last_pane_id": str(session.get("id") or ""),
                    "last_status": str(session.get("status") or "unknown"),
                    "last_seen_utc": now,
                    "seen_count": int(previous.get("seen_count") or 0) + 1,
                }
            ordered = sorted(existing.values(),
                             key=lambda item: str(item.get("last_seen_utc") or ""),
                             reverse=True)[:self.max_sessions]
            if sessions or not self.path.exists():
                self._write(ordered)
            return len(ordered)

    def entries(self) -> list[dict[str, Any]]:
        with self._lock:
            return self._read()

    def find(self, cli_session_id: str) -> list[dict[str, Any]]:
        wanted = str(cli_session_id or "")
        return [item for item in self.entries()
                if str(item.get("cli_session_id") or "") == wanted]
