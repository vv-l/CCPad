"""Single-window Feishu bridge demo for CCPad.

This pilot is deliberately a sidecar: it does not start a model, alter CCPad
state, or expose a public listener. CCPad's existing loopback WebTerminal
server remains the only terminal owner. The default mode requires an explicit
bind; isolated automated mode may bind only one unambiguous CLI pane and
announce the connection to the allow-listed Feishu chat.
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import json
import logging
import os
import re
import sys
import time
import hashlib
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import quote, urlparse, urlunparse

import requests
import websockets
from lark_oapi import EventDispatcherHandler, LogLevel
from feishu_async_ws import AsyncFeishuWsClient as FeishuWsClient

from offline_bridge_queue import OfflineBridgeQueue, OfflineQueueError
from session_index import SessionIndex, SessionIndexError


RESERVED_APP_FINGERPRINT = "eae420922385b408b4a4b11f0e1f412898a52c8506500e7d90b80791df39ca10"
ANSI_RE = re.compile(r"\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1b\\))")
CREDS_RE = {
    "app_id": re.compile(r"^\s*App ID:\s*`([^`]+)`\s*$", re.MULTILINE),
    "app_secret": re.compile(r"^\s*App Secret:\s*`([^`]+)`\s*$", re.MULTILINE),
}


def read_credentials(path: Path) -> tuple[str, str]:
    """Read credentials in memory; never print the file or its values."""
    env_app_id = os.environ.get("CCPAD_FEISHU_APP_ID", "").strip()
    env_app_secret = os.environ.get("CCPAD_FEISHU_APP_SECRET", "").strip()
    if env_app_id or env_app_secret:
        if not env_app_id or not env_app_secret:
            raise ValueError("both CCPAD_FEISHU_APP_ID and CCPAD_FEISHU_APP_SECRET are required")
        return env_app_id, env_app_secret
    content = path.read_text(encoding="utf-8")
    values: dict[str, str] = {}
    for key, pattern in CREDS_RE.items():
        match = pattern.search(content)
        if not match:
            raise ValueError(f"missing {key} in credentials file")
        values[key] = match.group(1)
    return values["app_id"], values["app_secret"]


class ReceiverOwnerBusy(RuntimeError):
    """A receiver for this App already owns the local user lock."""


def receiver_owner_path(app_id: str) -> Path:
    # State directories differ between pilots; ownership must not follow them.
    key = hashlib.sha256(app_id.encode("utf-8")).hexdigest()
    return Path.home() / ".ccpad-feishu" / "owners" / (key + ".lock")


class ReceiverOwnerLock:
    """OS byte/file lock; process death releases it without stealing PID files."""
    def __init__(self, path):
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        self.stream = path.open("a+b")
        try:
            if self.stream.seek(0, 2) == 0:
                self.stream.write(b"0")
                self.stream.flush()
            self.stream.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self.stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BaseException:
            self.stream.close()
            raise ReceiverOwnerBusy("receiver_owner_busy") from None

    def close(self):
        if self.stream.closed:
            return
        try:
            self.stream.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self.stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.stream.fileno(), fcntl.LOCK_UN)
        finally:
            self.stream.close()


def clean_terminal(text: str, limit: int = 2600) -> str:
    text = ANSI_RE.sub("", text).replace("\x00", "")
    text = text.replace("\r", "")
    lines = [line.rstrip() for line in text.splitlines()]
    while lines and not lines[0].strip():
        lines.pop(0)
    cleaned = "\n".join(lines).strip()
    if len(cleaned) > limit:
        cleaned = cleaned[-limit:]
        cleaned = "…" + cleaned
    return cleaned


def safe_url(base: str, path: str) -> str:
    parsed = urlparse(base)
    return urlunparse((parsed.scheme, parsed.netloc, path, "", "", ""))


@dataclass
class BridgeConfig:
    credentials: Path
    web_base: str
    web_token: str
    state_dir: Path
    listen_seconds: int
    allow_input: bool
    chat_id_file: Path | None = None
    web_pid: int | None = None
    allowed_chat_id: str | None = None
    web_descriptor: Path | None = None
    auto_bind_unique: bool = False
    adopt_first_chat: bool = False
    target_pane_id: str = ""
    window_id: str = ""
    generation: int = 0
    expected_app_id: str = ""
    startup_ready: Path | None = None
    public_source: dict[str, str] | None = None


def validate_owned_descriptor(config: BridgeConfig, descriptor: dict[str, Any]) -> None:
    if not config.target_pane_id:
        return
    if (descriptor.get("window_id") != config.window_id
            or descriptor.get("pane_id") != config.target_pane_id
            or descriptor.get("generation") != config.generation
            or descriptor.get("app_id") != config.expected_app_id
            or descriptor.get("pid") != config.web_pid
            or str(descriptor.get("base_url") or "").rstrip("/") != config.web_base
            or descriptor.get("token") != config.web_token):
        raise RuntimeError("owned_descriptor_identity_mismatch")
    if config.public_source and any(descriptor.get(key) != value
                                    for key, value in config.public_source.items()):
        raise RuntimeError("owned_public_source_mismatch")


def write_owned_ready(config: BridgeConfig) -> None:
    if config.startup_ready is None:
        return
    record = {"window_id": config.window_id, "pane_id": config.target_pane_id,
              "generation": config.generation, "app_id": config.expected_app_id,
              "ack_scope": "pty_write"}
    if config.public_source:
        record.update({key: config.public_source[key] for key in
                       ("engine", "cli_session_id", "session_provenance")})
        record["public_source"] = "codex_rollout_v1"
    staging = config.startup_ready.with_name(config.startup_ready.name + ".tmp")
    staging.write_text(json.dumps(record), encoding="utf-8")
    os.replace(staging, config.startup_ready)


def parse_message_content(message_type: str, content: dict[str, Any]) -> dict[str, str]:
    """Normalize text/image/file messages without retaining message bodies."""
    kind = (message_type or "text").lower()
    if kind == "text":
        return {"kind": "text", "text": str(content.get("text") or "").strip()}
    if kind == "image":
        return {"kind": "image", "resource_type": "image",
                "resource_key": str(content.get("image_key") or "")}
    if kind in {"file", "audio", "media", "video"}:
        return {"kind": kind, "resource_type": "file",
                "resource_key": str(content.get("file_key") or ""),
                "file_name": str(content.get("file_name") or "")}
    return {"kind": kind}


def safe_attachment_name(name: str, fallback: str) -> str:
    raw = Path(name).name if name else fallback
    raw = re.sub(r"[^A-Za-z0-9._-]+", "_", raw).strip("._")
    return (raw or fallback)[:120]


class FeishuApi:
    def __init__(self, app_id: str, app_secret: str) -> None:
        self.app_id = app_id
        self.app_secret = app_secret
        self._session = requests.Session()
        self._token = ""
        self._token_deadline = 0.0

    def _new_token(self) -> str:
        response = self._session.post(
            "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal",
            json={"app_id": self.app_id, "app_secret": self.app_secret},
            timeout=20,
        )
        body = response.json()
        if response.status_code != 200 or body.get("code") != 0:
            raise RuntimeError(f"Feishu authentication failed: http={response.status_code} code={body.get('code')}")
        token = body.get("tenant_access_token", "")
        if not token:
            raise RuntimeError("Feishu authentication returned no token")
        self._token_deadline = time.time() + int(body.get("expire", 7200) or 7200)
        return token

    def token(self) -> str:
        if self._token and time.time() < self._token_deadline - 60:
            return self._token
        self._token = self._new_token()
        return self._token

    def _request(self, method: str, path: str, **kwargs: Any) -> dict[str, Any]:
        headers = dict(kwargs.pop("headers", {}) or {})
        headers["Authorization"] = "Bearer " + self.token()
        response = self._session.request(method, "https://open.feishu.cn" + path,
                                         headers=headers, timeout=20, **kwargs)
        body = response.json() if response.content else {}
        if response.status_code >= 300 or body.get("code") not in (None, 0):
            raise RuntimeError(f"Feishu request failed: http={response.status_code} code={body.get('code')}")
        return body

    def list_messages(self, chat_id: str) -> list[dict[str, Any]]:
        body = self._request(
            "GET", "/open-apis/im/v1/messages",
            params={"container_id_type": "chat", "container_id": chat_id,
                    "page_size": 50, "sort_type": "ByCreateTimeAsc"},
        )
        return [x for x in (body.get("data") or {}).get("items", []) if isinstance(x, dict)]

    def reply(self, message_id: str, text: str) -> None:
        content = json.dumps({"text": text}, ensure_ascii=False)
        self._request("POST", f"/open-apis/im/v1/messages/{message_id}/reply",
                      json={"content": content, "msg_type": "text"})

    def send_text(self, chat_id: str, text: str) -> None:
        """Send a new text message to the allow-listed chat.

        Auto-binding has no inbound message ID to reply to, so the connection
        receipt uses the normal chat message endpoint.  The caller supplies the
        already allow-listed chat ID; credentials and tokens stay in memory.
        """
        if not chat_id:
            raise ValueError("missing Feishu chat id")
        content = json.dumps({"text": text}, ensure_ascii=False)
        self._request(
            "POST", "/open-apis/im/v1/messages",
            params={"receive_id_type": "chat_id"},
            json={"receive_id": chat_id, "msg_type": "text", "content": content},
        )

    def download_message_resource(self, message_id: str, resource_key: str,
                                  resource_type: str, target: Path,
                                  max_bytes: int = 100 * 1024 * 1024) -> None:
        """Download one resource from its source message into Demo staging.

        Feishu requires both the message ID and resource key for user-sent
        media. The response is streamed and capped before it reaches disk.
        """
        if not message_id or not resource_key:
            raise ValueError("missing message resource identity")
        if resource_type not in {"image", "file"}:
            raise ValueError("unsupported message resource type")
        path = (f"/open-apis/im/v1/messages/{quote(message_id, safe='')}/resources/"
                f"{quote(resource_key, safe='')}")
        response = self._session.get(
            "https://open.feishu.cn" + path,
            params={"type": resource_type},
            headers={
                "Authorization": "Bearer " + self.token(),
                "Content-Type": "application/json; charset=utf-8",
            },
            stream=True,
            timeout=30,
        )
        if response.status_code >= 300:
            raise RuntimeError(f"Feishu resource download failed: http={response.status_code}")
        target.parent.mkdir(parents=True, exist_ok=True)
        total = 0
        try:
            with target.open("wb") as fp:
                for chunk in response.iter_content(chunk_size=1024 * 1024):
                    if not chunk:
                        continue
                    total += len(chunk)
                    if total > max_bytes:
                        raise ValueError("message resource exceeds 100MB Demo limit")
                    fp.write(chunk)
        except Exception:
            try:
                target.unlink()
            except FileNotFoundError:
                pass
            raise


class InputRejected(RuntimeError):
    """The server proved no new PTY input was written."""


class TerminalLink:
    def __init__(self, config: BridgeConfig) -> None:
        self.config = config
        self._descriptor_mtime_ns: int | None = None
        self.websocket: websockets.WebSocketClientProtocol | None = None
        self.session_id = ""
        self.session_metadata: dict[str, Any] = {}
        self._reader: asyncio.Task[None] | None = None
        self._output = bytearray()
        self._output_lock = asyncio.Lock()
        self._send_lock = asyncio.Lock()
        self._attached = asyncio.Event()
        self._stop = asyncio.Event()
        self._ack_version = 0
        self._max_input_bytes = 0
        self._input_acks: dict[str, asyncio.Future[dict[str, Any]]] = {}
        self.ack_timeout = 10

    @property
    def state_file(self) -> Path:
        return self.config.state_dir / "feishu_single_window_binding.json"

    def load_binding_record(self) -> dict[str, Any]:
        try:
            body = json.loads(self.state_file.read_text(encoding="utf-8"))
            return body if isinstance(body, dict) else {}
        except (OSError, ValueError, TypeError):
            return {}

    def load_binding(self) -> str:
        body = self.load_binding_record()
        return str(body.get("pane_id") or body.get("session_id") or "")

    def save_binding(self, session_id: str, metadata: dict[str, Any] | None = None) -> None:
        metadata = metadata or {}
        self.config.state_dir.mkdir(parents=True, exist_ok=True)
        self.state_file.write_text(json.dumps({
            "pane_id": session_id,
            # Keep the old key for one-way compatibility with earlier Demo state.
            "session_id": session_id,
            "cli_session_id": str(metadata.get("sessionId") or ""),
            "cli_mode": str(metadata.get("cliMode") or ""),
            "working_dir": str(metadata.get("workingDir") or ""),
            "label": str(metadata.get("label") or ""),
            "web_pid": self.config.web_pid,
            "web_base": self.config.web_base,
            "saved_at_utc": datetime.now(timezone.utc).isoformat(),
        }, ensure_ascii=False, indent=2), encoding="utf-8")

    def clear_binding(self) -> None:
        try:
            self.state_file.unlink()
        except FileNotFoundError:
            pass

    @staticmethod
    def _stable_matches(saved: dict[str, Any], current: dict[str, Any]) -> bool:
        saved_cli = str(saved.get("cli_session_id") or "")
        current_cli = str(current.get("sessionId") or "")
        if not saved_cli or saved_cli != current_cli:
            return False
        saved_mode = str(saved.get("cli_mode") or "")
        current_mode = str(current.get("cliMode") or "")
        if saved_mode and saved_mode != current_mode:
            return False
        saved_dir = str(saved.get("working_dir") or "")
        current_dir = str(current.get("workingDir") or "")
        return not saved_dir or saved_dir == current_dir

    def resolve_saved_session(self, sessions: list[dict[str, Any]]) -> dict[str, Any] | None:
        """Resolve a saved binding without silently attaching a different pane.

        Pane IDs are valid only inside one CCPad process. After the WebTerminal
        descriptor PID changes, require a unique CLI session match plus the saved
        mode/workspace metadata.
        """
        saved = self.load_binding_record()
        if not saved:
            return None
        pane_id = str(saved.get("pane_id") or saved.get("session_id") or "")
        saved_pid = saved.get("web_pid")
        same_process = saved_pid is None or self.config.web_pid is None or saved_pid == self.config.web_pid
        if same_process and pane_id:
            exact = [x for x in sessions if str(x.get("id") or "") == pane_id]
            if len(exact) == 1:
                return exact[0]
        stable = [x for x in sessions if self._stable_matches(saved, x)]
        return stable[0] if len(stable) == 1 else None

    def bound_summary(self) -> dict[str, Any]:
        if self.session_metadata:
            return dict(self.session_metadata)
        return self.load_binding_record()

    def list_sessions(self) -> list[dict[str, Any]]:
        self.refresh_descriptor()
        url = safe_url(self.config.web_base, "/api/sessions")
        params = {"token": self.config.web_token} if self.config.web_token else {}
        response = requests.get(url, params=params, timeout=15)
        if response.status_code != 200:
            raise RuntimeError(f"CCPad session list failed: http={response.status_code}")
        body = response.json()
        if not isinstance(body, list):
            raise RuntimeError("CCPad session list returned an unexpected response")
        return [x for x in body if isinstance(x, dict)]

    def refresh_descriptor(self) -> bool:
        """Refresh the isolated Demo endpoint after a Demo restart."""
        path = self.config.web_descriptor
        if path is None:
            return False
        try:
            stat = path.stat()
            if not self.config.target_pane_id and self._descriptor_mtime_ns == stat.st_mtime_ns:
                return False
            descriptor = json.loads(path.read_text(encoding="utf-8"))
            base = str(descriptor["base_url"]).rstrip("/")
            token = str(descriptor["token"])
            pid_value = descriptor.get("pid")
            pid = int(pid_value) if pid_value is not None else None
        except (OSError, ValueError, TypeError, KeyError):
            if self.config.target_pane_id:
                raise RuntimeError("owned_descriptor_unavailable") from None
            return False
        validate_owned_descriptor(self.config, descriptor)
        changed = (base != self.config.web_base or token != self.config.web_token
                   or pid != self.config.web_pid)
        self.config.web_base = base
        self.config.web_token = token
        self.config.web_pid = pid
        self._descriptor_mtime_ns = stat.st_mtime_ns
        return changed

    def _ws_url(self) -> str:
        self.refresh_descriptor()
        parsed = urlparse(self.config.web_base)
        scheme = "wss" if parsed.scheme == "https" else "ws"
        query = f"token={self.config.web_token}" if self.config.web_token else ""
        return urlunparse((scheme, parsed.netloc, "/ws", "", query, ""))

    async def close(self) -> None:
        self._stop.set()
        if self._reader:
            self._reader.cancel()
            await asyncio.gather(self._reader, return_exceptions=True)
            self._reader = None
        if self.websocket:
            try:
                await self.websocket.close()
            except Exception:
                pass
        self.websocket = None
        self._attached.clear()

    async def bind(self, session_id: str, selected: dict[str, Any] | None = None) -> dict[str, Any]:
        if self.config.target_pane_id and session_id != self.config.target_pane_id:
            raise RuntimeError("owned_target_pane_mismatch")
        if selected is None:
            sessions = await asyncio.to_thread(self.list_sessions)
            selected = next((x for x in sessions if x.get("id") == session_id), None)
        if selected is None:
            raise RuntimeError("session id was not found in CCPad")
        if self.config.target_pane_id:
            self.refresh_descriptor()
            if selected.get("id") != session_id:
                raise RuntimeError("owned_target_metadata_mismatch")
        await self.close()
        self._stop = asyncio.Event()
        self.session_id = session_id
        self.session_metadata = dict(selected)
        self.websocket = await websockets.connect(self._ws_url(), open_timeout=15,
                                                  close_timeout=3, max_size=8 * 1024 * 1024)
        await self._send({"type": "attach", "sessionId": session_id})
        self._reader = asyncio.create_task(self._read_loop())
        try:
            await asyncio.wait_for(self._attached.wait(), timeout=10)
        except asyncio.TimeoutError as exc:
            await self.close()
            raise RuntimeError("CCPad did not confirm the terminal attachment") from exc
        self.save_binding(session_id, selected)
        return selected

    async def unbind(self) -> None:
        await self.close()
        self.session_id = ""
        self.session_metadata = {}
        self.clear_binding()

    async def send_input(self, text: str, input_id: str, *, expected_turn_id: str = "") -> dict[str, Any]:
        if not self.websocket or not self._attached.is_set():
            raise InputRejected("no CCPad session is bound")
        if self._ack_version != 1:
            raise InputRejected("input_ack_unavailable")
        payload = {"type": "input", "data": text, "inputId": input_id,
                   "sessionId": self.session_id}
        if self.config.public_source:
            payload["expectedCliSessionId"] = self.config.public_source["cli_session_id"]
            payload["expectedSessionProvenance"] = self.config.public_source["session_provenance"]
            if expected_turn_id:
                payload["expectedTurnId"] = expected_turn_id
        raw = json.dumps(payload, ensure_ascii=False)
        if len(raw.encode("utf-8")) > self._max_input_bytes:
            raise InputRejected("input_frame_too_large")
        future = asyncio.get_running_loop().create_future()
        self._input_acks[input_id] = future
        async with self._send_lock:
            try:
                await self.websocket.send(raw)
                ack = await asyncio.wait_for(future, self.ack_timeout)
                if ack.get("status") == "written" and ack.get("ackScope") == "pty_write":
                    return ack
                if ack.get("status") == "rejected" and ack.get("errorCode") in (
                        "not_attached", "pane_mismatch", "input_history_full", "cli_identity_mismatch"):
                    raise InputRejected(str(ack["errorCode"]))
                raise RuntimeError("input_delivery_unknown")
            finally:
                self._input_acks.pop(input_id, None)

    async def _send(self, payload: dict[str, Any]) -> None:
        if not self.websocket:
            raise RuntimeError("CCPad websocket is not connected")
        async with self._send_lock:
            await self.websocket.send(json.dumps(payload, ensure_ascii=False))

    async def _read_loop(self) -> None:
        assert self.websocket is not None
        try:
            async for raw in self.websocket:
                if isinstance(raw, bytes):
                    continue
                try:
                    message = json.loads(raw)
                except ValueError:
                    continue
                kind = message.get("type")
                if kind == "attached" and message.get("sessionId") == self.session_id:
                    self._ack_version = message.get("inputAckVersion", 0)
                    self._max_input_bytes = message.get("maxInputMessageBytes", 0)
                    self._attached.set()
                elif kind == "input_ack" and message.get("sessionId") == self.session_id:
                    future = self._input_acks.get(str(message.get("inputId") or ""))
                    if future is not None and not future.done():
                        future.set_result(message)
                elif kind in ("replay", "output"):
                    # TUI bytes are not a public event source, even in legacy pilot mode.
                    continue
                elif kind == "error":
                    self._attached.clear()
        except asyncio.CancelledError:
            raise
        except Exception:
            self._attached.clear()

    async def drain_output(self) -> str:
        async with self._output_lock:
            if not self._output:
                return ""
            raw = bytes(self._output)
            self._output.clear()
        return clean_terminal(raw.decode("utf-8", errors="replace"))


class BridgeDemo:
    def __init__(self, config: BridgeConfig, *, credentials: tuple[str, str] | None = None) -> None:
        app_id, app_secret = credentials or read_credentials(config.credentials)
        self.config = config
        self.feishu = FeishuApi(app_id, app_secret)
        self.terminal = TerminalLink(config)
        self.offline_queue = OfflineBridgeQueue(
            config.state_dir / "offline-message-queue.json"
        )
        self.session_index = SessionIndex(config.state_dir / "session-index.json")
        self.allowed_chat_id = config.allowed_chat_id or self._load_allowed_chat_id()
        self.loop = asyncio.get_running_loop()
        self.started = time.monotonic()
        self.started_at_ms = int(time.time() * 1000)
        self.events = 0
        self.binding_state = "unbound"
        self._binding_lock = asyncio.Lock()
        self._auto_notice_key = ""
        self._r5 = None
        if config.public_source:
            from r5_bridge import PublicTaskBridge
            self._r5 = PublicTaskBridge(self)
        self._seen_message_hashes = self._load_seen_hashes()
        self.handler = (
            EventDispatcherHandler.builder("", "", level=LogLevel.ERROR)
            .register_p2_im_message_receive_v1(self._on_event)
            .register_p2_card_action_trigger(self._on_card_action)
            .build()
        )
        self.ws_client = FeishuWsClient(
            app_id, app_secret, log_level=LogLevel.ERROR,
            event_handler=self.handler, auto_reconnect=False,
        )

    def _load_allowed_chat_id(self) -> str:
        if not self.config.chat_id_file:
            return ""
        try:
            body = json.loads(self.config.chat_id_file.read_text(encoding="utf-8"))
            return str(body.get("chat_id") or "") if isinstance(body, dict) else ""
        except (OSError, ValueError, TypeError):
            return ""

    def _adopt_first_chat(self, chat_id: str) -> bool:
        """Optionally lock an unconfigured isolated pilot to its first chat."""
        if not self.config.adopt_first_chat or self.allowed_chat_id or not chat_id:
            return False
        self.allowed_chat_id = chat_id
        if self.config.auto_bind_unique and self.terminal._attached.is_set():
            self.loop.create_task(self._announce_auto_binding(self.terminal.session_metadata))
        return True

    def _can_deliver_input(self) -> bool:
        if getattr(self, "_r5", None) and self._r5.busy:
            return False
        if self.config.target_pane_id:
            self.terminal.refresh_descriptor()
            if self.terminal.session_id != self.config.target_pane_id:
                return False
        return bool(self.config.allow_input and self.terminal.session_id
                    and self.terminal._attached.is_set())

    @staticmethod
    def _is_bridge_command(text: str) -> bool:
        return text.startswith("/bridge")

    def _remember_sessions(self, sessions: list[dict[str, Any]]) -> None:
        try:
            self.session_index.record(sessions)
        except SessionIndexError:
            pass

    def _history_text(self) -> str:
        try:
            entries = self.session_index.entries()
        except SessionIndexError:
            return "历史 Session 索引暂不可读。"
        if not entries:
            return "暂无历史 Session 记录。"
        lines = []
        for item in entries[:50]:
            cli_id = str(item.get("cli_session_id") or "未记录")
            mode = str(item.get("cli_mode") or "未知引擎")
            working_dir = str(item.get("working_dir") or "未记录")
            label = str(item.get("label") or "terminal")
            status = str(item.get("last_status") or "unknown")
            seen = str(item.get("last_seen_utc") or "未知时间")
            lines.append(f"{label} · {mode}/{status} · CLI Session {cli_id}\n"
                         f"目录：{working_dir}\n最后观测：{seen}")
        return "历史 Session（仅供显式恢复参考）：\n" + "\n".join(lines)

    def _mark_seen(self, message_id: str) -> None:
        self._last_message_id = message_id
        self._seen_message_hashes.add(self._message_hash(message_id))
        self._save_seen_hashes()

    def _queue_payload(self, message_id: str, chat_id: str,
                       parsed: dict[str, str]) -> bool:
        try:
            queued = self.offline_queue.enqueue(
                message_id,
                chat_id,
                str(parsed.get("kind") or "text"),
                text=str(parsed.get("text") or ""),
                payload=parsed,
            )
            if queued:
                return True
            key = self._message_hash(message_id)
            return any(str(item.get("message_id_sha256") or "") == key
                       for item in self.offline_queue.pending())
        except (OfflineQueueError, ValueError):
            return False

    async def _reply_queued(self, message_id: str, kind: str) -> None:
        label = "附件" if kind != "text" else "消息"
        try:
            await self.reply(message_id, f"CCPad 当前不可达，{label}已暂存；恢复绑定后按顺序发送。")
        except Exception:
            pass

    async def _try_reconnect_saved(self) -> bool:
        """Reconnect only to a uniquely resolved saved CLI session."""
        if self.terminal._attached.is_set():
            return False
        saved = self.terminal.load_binding_record()
        if not saved:
            return False
        async with self._binding_lock:
            if self.terminal._attached.is_set():
                return False
            try:
                sessions = await asyncio.to_thread(self.terminal.list_sessions)
                self._remember_sessions(sessions)
                selected = self.terminal.resolve_saved_session(sessions)
                if selected is None:
                    self.binding_state = "orphaned"
                    return False
                await self.terminal.bind(str(selected.get("id") or ""), selected)
                self.binding_state = "reconnected"
                await self.flush_offline_queue()
                return True
            except Exception:
                self.binding_state = "suspended"
                return False

    async def _bind_cli_session(self, cli_session_id: str) -> dict[str, Any] | None:
        """Bind one unique CLI Session and flush any pending pane input."""
        wanted = str(cli_session_id or "").strip()
        if not wanted:
            return None
        async with self._binding_lock:
            sessions = await asyncio.to_thread(self.terminal.list_sessions)
            self._remember_sessions(sessions)
            candidates = [x for x in sessions
                          if str(x.get("sessionId") or "") == wanted]
            # Some local Codex builds do not expose a stable thread ID until
            # the first turn. An exact Pane ID remains a safe explicit choice.
            if not candidates:
                candidates = [x for x in sessions
                              if str(x.get("id") or "") == wanted]
            if len(candidates) != 1:
                return None
            selected = candidates[0]
            await self.terminal.bind(str(selected.get("id") or ""), selected)
            self.binding_state = "reconnected"
            await self.flush_offline_queue()
            return selected

    async def _try_auto_bind_unique(self) -> dict[str, Any] | None:
        """Bind the only visible CLI pane when explicit automation is enabled.

        The guard deliberately refuses zero or multiple panes.  A sidecar must
        never guess a target when the isolated CCPad has more than one session.
        """
        if not self.config.auto_bind_unique or self.terminal._attached.is_set():
            return None
        async with self._binding_lock:
            if self.terminal._attached.is_set():
                return None
            sessions = await asyncio.to_thread(self.terminal.list_sessions)
            self._remember_sessions(sessions)
            cli_sessions = [
                session for session in sessions
                if str(session.get("cliMode") or "").strip()
            ]
            if len(cli_sessions) == 0:
                self.binding_state = "waiting_cli_session"
                return None
            if len(cli_sessions) != 1:
                self.binding_state = "ambiguous"
                return None
            selected = cli_sessions[0]
            pane_id = str(selected.get("id") or "").strip()
            if not pane_id:
                self.binding_state = "waiting_pane_id"
                return None
            await self.terminal.bind(pane_id, selected)
            self.binding_state = "active"
            await self.flush_offline_queue()
            return selected

    async def _announce_auto_binding(self, session: dict[str, Any]) -> None:
        """Send one connection receipt after an automatic bind succeeds."""
        chat_id = str(self.allowed_chat_id or "")
        if not chat_id:
            return
        key = ":".join([
            str(self.config.web_pid or ""),
            str(session.get("id") or ""),
            str(session.get("sessionId") or ""),
        ])
        if not key or key == getattr(self, "_auto_notice_key", ""):
            return
        text = self._connection_summary(session) + "\n接入方式：隔离 Demo 自动绑定唯一 CLI 窗口。"
        try:
            await asyncio.to_thread(self.feishu.send_text, chat_id, text)
        except Exception:
            return
        self._auto_notice_key = key
        try:
            self.config.state_dir.mkdir(parents=True, exist_ok=True)
            (self.config.state_dir / "auto-connection-notice.json").write_text(
                json.dumps({
                    "sent_at_utc": datetime.now(timezone.utc).isoformat(),
                    "binding_key": key,
                }, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
        except OSError:
            pass

    def _connection_summary(self, session: dict[str, Any]) -> str:
        """Return a metadata-only connection receipt; do not start a model turn."""
        cli_id = str(session.get("sessionId") or "未记录")
        mode = str(session.get("cliMode") or "未知引擎")
        working_dir = str(session.get("workingDir") or "未记录")
        pane_id = str(session.get("id") or "未记录")
        status = str(session.get("status") or "unknown")
        last_activity = str(session.get("lastActivityUtc") or "未记录")
        try:
            queued = len(self.offline_queue.pending())
        except OfflineQueueError:
            queued = -1
        queued_text = "不可读取" if queued < 0 else str(queued)
        return ("CCPad Bot 接入完成\n"
                "接入任务摘要（元数据，不启动新的模型回合）：\n"
                f"Pane：{pane_id}\nCLI Session：{cli_id}\n引擎：{mode}\n"
                f"目录：{working_dir}\n状态：{status}\n最后活动：{last_activity}\n"
                "上下文：继续使用该 CCPad 原 CLI Session；不会另起 AutoBot 会话。\n"
                "图片/文件：可发送，先进入 Demo 专属 staging，再注入当前 Session。\n"
                f"待发送队列：{queued_text}")

    def _queue_if_offline(self, message_id: str, chat_id: str,
                          parsed: dict[str, str]) -> bool:
        """Durably accept pane-bound input before marking it seen."""
        if not self.config.allow_input or self._can_deliver_input():
            return False
        if parsed.get("kind") == "text" and not str(parsed.get("text") or "").strip():
            return False
        queued = self._queue_payload(message_id, chat_id, parsed)
        if queued:
            self._mark_seen(message_id)
            self._record_event_values(
                message_id, "ws", "", str(parsed.get("kind") or "text"),
                "offline_queued",
            )
            self.loop.create_task(self._reply_queued(
                message_id, str(parsed.get("kind") or "text")))
        return queued

    def _on_card_action(self, data: Any):
        from lark_oapi.event.callback.model.p2_card_action_trigger import P2CardActionTriggerResponse
        accepted = False
        try:
            event = data.event
            value = event.action.value
            context = event.context
            if (self._r5 and isinstance(value, dict) and value.get("action") == "abort"
                    and context.open_chat_id == self.allowed_chat_id
                    and value.get("chat_id") == self.allowed_chat_id):
                accepted = self._r5.cards.abort(value.get("task_id"), self.allowed_chat_id,
                                                context.open_message_id)
        except (AttributeError, TypeError, ValueError):
            pass
        return P2CardActionTriggerResponse({"toast": {
            "type": "info", "content": "已提交中止请求，等待会话确认。" if accepted
            else "该卡片当前没有可中止的已关联任务。"}})

    def _on_event(self, data: Any) -> None:
        # The SDK has already validated and decoded the event. Schedule all
        # work so the WS ACK is returned quickly and no model runner is called.
        self.events += 1
        try:
            message = data.event.message
            message_id = str(message.message_id or "")
            content = json.loads(message.content or "{}")
            message_type = str(getattr(message, "message_type", "text") or "text")
            parsed = parse_message_content(message_type, content)
            chat_id = str(message.chat_id or "")
        except Exception:
            return
        if message_id and chat_id:
            if self._message_hash(message_id) in self._seen_message_hashes:
                return
            if self.config.target_pane_id:
                try:
                    created_ms = int(str(getattr(message, "create_time", "0") or "0"))
                except ValueError:
                    return
                if created_ms < self.started_at_ms:
                    return
            self._adopt_first_chat(chat_id)
            if self.allowed_chat_id and chat_id != self.allowed_chat_id:
                self._mark_seen(message_id)
                self._record_event_values(message_id, "ws", str(getattr(message, "chat_type", "")),
                                          message_type, "ignored_chat")
                return
            text = str(parsed.get("text") or "")
            if (parsed.get("kind") != "text" or not self._is_bridge_command(text)) \
                    and self._queue_if_offline(message_id, chat_id, parsed):
                return
            self._mark_seen(message_id)
            self._record_event(message, str(parsed.get("text") or ""),
                               str(parsed.get("kind") or message_type))
            if parsed.get("kind") == "text":
                self.loop.create_task(self.handle_text(message_id, chat_id,
                                                        str(parsed.get("text") or "")))
            elif parsed.get("resource_key"):
                self.loop.create_task(self.handle_attachment(message_id, chat_id, parsed))

    @staticmethod
    def _message_hash(message_id: str) -> str:
        return hashlib.sha256(message_id.encode()).hexdigest()

    def _seen_path(self) -> Path:
        return self.config.state_dir / "seen-message-hashes.json"

    def _load_seen_hashes(self) -> set[str]:
        try:
            values = json.loads(self._seen_path().read_text(encoding="utf-8"))
            return {str(x) for x in values if isinstance(x, str)}
        except (OSError, ValueError, TypeError):
            return set()

    def _save_seen_hashes(self) -> None:
        try:
            self.config.state_dir.mkdir(parents=True, exist_ok=True)
            recent = list(self._seen_message_hashes)[-500:]
            self._seen_path().write_text(json.dumps(recent), encoding="utf-8")
        except OSError:
            pass

    def _record_event(self, message: Any, text: str, message_kind: str = "text") -> None:
        """Persist only routing evidence; never persist message text or IDs."""
        category = ("bridge_status" if text == "/bridge status" else
                    "bridge_command" if text.startswith("/bridge") else
                    "attachment" if message_kind != "text" else "text")
        self._record_event_values(
            str(message.message_id), "ws", str(getattr(message, "chat_type", "")),
            str(getattr(message, "message_type", "")), category,
        )

    def _record_event_values(self, message_id: str, source: str, chat_type: str,
                             message_type: str, category: str) -> None:
        try:
            self.config.state_dir.mkdir(parents=True, exist_ok=True)
            row = {
                "received_at_utc": datetime.now(timezone.utc).isoformat(),
                "source": source,
                "message_id_sha256": hashlib.sha256(message_id.encode()).hexdigest(),
                "chat_type": chat_type,
                "message_type": message_type,
                "category": category,
                "model_calls": 0,
            }
            with (self.config.state_dir / "bridge-events.jsonl").open("a", encoding="utf-8") as fp:
                fp.write(json.dumps(row, ensure_ascii=False) + "\n")
        except Exception:
            pass

    async def reply(self, message_id: str, text: str) -> None:
        await asyncio.to_thread(self.feishu.reply, message_id, text)

    async def handle_text(self, message_id: str, chat_id: str, text: str) -> None:
        try:
            if text == "/bridge status":
                await self.reply(message_id, await self.status_text())
                return
            if text == "/bridge whoami":
                await self.reply(message_id, await self.status_text())
                return
            if text.startswith("/bridge bind "):
                session_id = text[len("/bridge bind "):].strip()
                async with self._binding_lock:
                    selected = await self.terminal.bind(session_id)
                    self._remember_sessions([selected])
                    self.binding_state = "active"
                    await self.flush_offline_queue()
                label = selected.get("label") or session_id
                await self.reply(message_id, self._session_confirmation("已绑定 CCPad 单窗口", selected, label))
                return
            if text.startswith("/bridge connect "):
                cli_session_id = text[len("/bridge connect "):].strip()
                selected = await self._bind_cli_session(cli_session_id)
                if selected is None:
                    await self.reply(message_id, "无法唯一接入该 CLI Session；请先执行 /bridge sessions。")
                    return
                await self.reply(message_id, self._connection_summary(selected))
                return
            if text.startswith("/bridge resume "):
                cli_session_id = text[len("/bridge resume "):].strip()
                selected = await self._bind_cli_session(cli_session_id)
                if selected is None:
                    await self.reply(message_id, "无法唯一恢复该 CLI Session；请先执行 /bridge sessions。")
                    return
                label = selected.get("label") or cli_session_id
                await self.reply(message_id, self._session_confirmation("已恢复 CCPad Session", selected, label))
                return
            if text == "/bridge unbind":
                async with self._binding_lock:
                    await self.terminal.unbind()
                    self.binding_state = "unbound"
                await self.reply(message_id, "已解除 CCPad 单窗口绑定。")
                return
            if text == "/bridge sessions":
                sessions = await asyncio.to_thread(self.terminal.list_sessions)
                self._remember_sessions(sessions)
                if not sessions:
                    await self.reply(message_id, "CCPad 当前没有可见终端会话。")
                else:
                    lines = [self._session_line(x)
                             for x in sessions]
                    await self.reply(message_id, "可绑定会话：\n" + "\n".join(lines))
                return
            if text == "/bridge history":
                await self.reply(message_id, self._history_text())
                return
            if text.startswith("/bridge"):
                await self.reply(message_id,
                                 "命令：/bridge status、/bridge whoami、/bridge sessions、"
                                 "/bridge history、/bridge bind <pane-id>、"
                                 "/bridge connect <cli-session-id>、"
                                 "/bridge resume <cli-session-id>、/bridge unbind")
                return
            if not text:
                return
            if not self.config.allow_input:
                await self.reply(message_id, "Demo 当前是只读模式；启动时显式加入 --allow-input 后，绑定窗口才会接收普通消息。")
                return
            payload = {"kind": "text", "text": text}
            if not self._can_deliver_input():
                if self._queue_payload(message_id, chat_id, payload):
                    await self._reply_queued(message_id, "text")
                    return
                raise RuntimeError("CCPad session is unavailable and offline queue rejected the message")
            try:
                if not self._queue_payload(message_id, chat_id, payload):
                    raise RuntimeError("offline queue rejected the message")
                await self._deliver_queued(message_id, text + "\r")
            except Exception:
                if self._queue_payload(message_id, chat_id, payload):
                    await self._reply_queued(message_id, "text")
                    return
                raise
            await self.reply(message_id, "已发送到绑定的 CCPad 窗口，等待终端输出。")
        except Exception as exc:
            await self.reply(message_id, f"Demo 操作失败：{type(exc).__name__}")

    @staticmethod
    def _session_line(session: dict[str, Any]) -> str:
        pane_id = str(session.get("id") or "?")
        label = str(session.get("label") or session.get("command") or "terminal")
        cli_id = str(session.get("sessionId") or "no-session-id")
        mode = str(session.get("cliMode") or "shell")
        status = str(session.get("status") or "unknown")
        return f"{pane_id} · {label} · {mode}/{status} · CLI Session {cli_id}"

    @staticmethod
    def _session_confirmation(prefix: str, session: dict[str, Any], label: str) -> str:
        cli_id = str(session.get("sessionId") or "未记录")
        mode = str(session.get("cliMode") or "未知引擎")
        working_dir = str(session.get("workingDir") or "未记录")
        return (f"{prefix}：{label}\n"
                f"Pane: {session.get('id') or '未记录'}\n"
                f"CLI Session: {cli_id}\n引擎：{mode}\n目录：{working_dir}")

    def _attachment_target(self, message_id: str, payload: dict[str, str]) -> Path:
        key = payload.get("resource_key") or "resource"
        digest = hashlib.sha256(f"{message_id}:{key}".encode()).hexdigest()[:16]
        kind = payload.get("kind") or "file"
        fallback = "image.bin" if kind == "image" else f"{kind}.bin"
        name = safe_attachment_name(payload.get("file_name") or "", fallback)
        return self.config.state_dir / "attachments" / f"{digest}-{name}"

    async def handle_attachment(self, message_id: str, chat_id: str,
                                payload: dict[str, str]) -> None:
        if not self.config.allow_input:
            await self.reply(message_id, "Demo 当前是只读模式；不会下载或转发附件。")
            return
        if not self._can_deliver_input():
            if self._queue_payload(message_id, chat_id, payload):
                await self._reply_queued(message_id, str(payload.get("kind") or "file"))
                return
            await self.reply(message_id, "当前没有可用的 CCPad 窗口，且离线队列未能接收附件。")
            return
        resource_key = payload.get("resource_key") or ""
        resource_type = payload.get("resource_type") or "file"
        target = self._attachment_target(message_id, payload)
        try:
            if not target.exists():
                await asyncio.to_thread(
                    self.feishu.download_message_resource,
                    message_id, resource_key, resource_type, target,
                )
            kind = payload.get("kind") or "附件"
            prompt = (f"[飞书{kind}附件] 已保存到本机隔离 staging：{target}\n"
                      "请在当前 CCPad 会话中按需处理这个附件。")
            if not self._queue_payload(message_id, chat_id, payload):
                raise RuntimeError("offline queue rejected the attachment")
            await self._deliver_queued(message_id, prompt + "\r")
            await self.reply(message_id, f"附件已转发到当前 CCPad 会话：{target.name}")
        except Exception as exc:
            if self._queue_payload(message_id, chat_id, payload):
                await self._reply_queued(message_id, str(payload.get("kind") or "file"))
                return
            await self.reply(message_id, f"附件处理失败：{type(exc).__name__}")

    async def _deliver_queued(self, message_id: str, text: str) -> None:
        if getattr(self, "_r5", None):
            await self._r5.deliver(message_id, text.rstrip("\r"))
            return
        input_id = self.offline_queue.begin_delivery(message_id)
        try:
            ack = await self.terminal.send_input(text, input_id)
        except InputRejected:
            self.offline_queue.mark_rejected(message_id)
            raise
        if (not isinstance(ack, dict) or ack.get("inputId") != input_id
                or ack.get("sessionId") != self.terminal.session_id
                or ack.get("status") != "written" or ack.get("ackScope") != "pty_write"):
            raise RuntimeError("input_delivery_unknown")
        if not self.offline_queue.ack(message_id, input_id):
            raise OfflineQueueError("confirmed input could not be removed")

    async def flush_offline_queue(self) -> int:
        """Send queued pane-bound inputs in order after a valid rebind."""
        if not self._can_deliver_input():
            return 0
        delivered = 0
        for item in self.offline_queue.pending():
            message_id = str(item.get("message_id") or "")
            chat_id = str(item.get("chat_id") or "")
            kind = str(item.get("kind") or "text")
            payload = item.get("payload") if isinstance(item.get("payload"), dict) else {}
            try:
                if kind == "text":
                    text = str(item.get("text") or payload.get("text") or "")
                    if not text:
                        raise ValueError("queued text is empty")
                    await self._deliver_queued(message_id, text + "\r")
                    reply_text = "离线暂存的消息已按顺序发送到 CCPad 窗口。"
                else:
                    target = self._attachment_target(message_id, {
                        str(key): str(value) for key, value in payload.items()
                    })
                    resource_key = str(payload.get("resource_key") or "")
                    resource_type = str(payload.get("resource_type") or "file")
                    if not target.exists():
                        await asyncio.to_thread(
                            self.feishu.download_message_resource,
                            message_id, resource_key, resource_type, target,
                        )
                    label = str(payload.get("kind") or "附件")
                    prompt = (f"[飞书{label}附件] 已保存到本机隔离 staging：{target}\n"
                              "请在当前 CCPad 会话中按需处理这个附件。")
                    await self._deliver_queued(message_id, prompt + "\r")
                    reply_text = "离线暂存的附件已按顺序发送到 CCPad 窗口。"
                delivered += 1
                if getattr(self, "_r5", None):
                    break  # A PTY ACK is not a completed model task.
                if chat_id:
                    try:
                        await self.reply(message_id, reply_text)
                    except Exception:
                        pass
            except Exception as exc:
                self.offline_queue.mark_failed(message_id, type(exc).__name__)
                break
        return delivered

    async def status_text(self) -> str:
        try:
            sessions = await asyncio.to_thread(self.terminal.list_sessions)
            self._remember_sessions(sessions)
        except Exception:
            return (f"单窗口 Demo 在线；入站事件 {self.events}；模型调用 0\n"
                    f"绑定状态：{self.binding_state}；CCPad WebTerminal 当前不可达\n"
                    f"普通消息转发：{'开启' if self.config.allow_input else '只读'}")
        bound = self.terminal.session_id or self.terminal.load_binding() or "未绑定"
        metadata = self.terminal.bound_summary()
        cli_id = str(metadata.get("sessionId") or metadata.get("cli_session_id") or "未记录")
        mode = str(metadata.get("cliMode") or metadata.get("cli_mode") or "未知引擎")
        pane_status = str(metadata.get("status") or "unknown")
        label = str(metadata.get("label") or "未命名窗口")
        return (f"单窗口 Demo 在线；入站事件 {self.events}；模型调用 0\n"
                f"绑定状态：{self.binding_state}；Pane：{bound}\n"
                f"窗口：{label}；引擎：{mode}；CLI Session：{cli_id}\n"
                f"Pane 状态：{pane_status}；CCPad 会话：{len(sessions)} 个\n"
                f"允许聊天：{'已限制' if self.allowed_chat_id else '未配置'}\n"
                f"普通消息转发：{'开启' if self.config.allow_input else '只读'}")

    async def output_loop(self) -> None:
        while True:
            await asyncio.sleep(0.5)
            if getattr(self, "_r5", None):
                await self._r5.poll()

    async def message_poll_loop(self) -> None:
        """Compensate for a missed WS event for the one demo chat."""
        if not self.config.chat_id_file:
            return
        while True:
            try:
                payload = json.loads(self.config.chat_id_file.read_text(encoding="utf-8"))
                chat_id = str(payload.get("chat_id") or "")
                if chat_id:
                    messages = await asyncio.to_thread(self.feishu.list_messages, chat_id)
                    for item in messages:
                        message_id = str(item.get("message_id") or "")
                        if not message_id or self._message_hash(message_id) in self._seen_message_hashes:
                            continue
                        # REST polling sees the last 50 messages, so fence out
                        # everything that predates this fresh manual-trial run.
                        try:
                            created_ms = int(str(item.get("create_time") or "0"))
                        except (TypeError, ValueError):
                            created_ms = 0
                        if created_ms and created_ms < self.started_at_ms:
                            self._mark_seen(message_id)
                            continue
                        if self.allowed_chat_id and chat_id != self.allowed_chat_id:
                            self._seen_message_hashes.add(self._message_hash(message_id))
                            continue
                        sender_type = str((item.get("sender") or {}).get("sender_type") or "")
                        if sender_type != "user":
                            self._seen_message_hashes.add(self._message_hash(message_id))
                            continue
                        raw = (item.get("body") or {}).get("content") or "{}"
                        try:
                            msg_type = str(item.get("msg_type") or "text")
                            parsed = parse_message_content(msg_type, json.loads(raw))
                        except (TypeError, ValueError):
                            parsed = {"kind": "text", "text": ""}
                        text = str(parsed.get("text") or "")
                        if (parsed.get("kind") != "text" or not self._is_bridge_command(text)) \
                                and self._queue_if_offline(message_id, chat_id, parsed):
                            continue
                        self._mark_seen(message_id)
                        self.events += 1
                        category = "bridge_status" if text == "/bridge status" else (
                            "bridge_command" if text.startswith("/bridge") else
                            "attachment" if parsed.get("resource_key") else "text")
                        self._record_event_values(message_id, "poll", item.get("chat_type", ""),
                                                  item.get("msg_type", ""), category)
                        if parsed.get("kind") == "text" and text:
                            asyncio.create_task(self.handle_text(message_id, chat_id, text))
                        elif parsed.get("resource_key"):
                            asyncio.create_task(self.handle_attachment(message_id, chat_id, parsed))
            except Exception:
                pass
            await asyncio.sleep(3)

    async def binding_reconnect_loop(self) -> None:
        """Retry a saved binding without ever selecting an unrecorded pane."""
        while True:
            await asyncio.sleep(3)
            if self.terminal._attached.is_set():
                continue
            if await self._try_reconnect_saved():
                if self.config.auto_bind_unique:
                    await self._announce_auto_binding(self.terminal.session_metadata)
                continue
            try:
                selected = await self._try_auto_bind_unique()
                if selected is not None:
                    await self._announce_auto_binding(selected)
            except Exception:
                self.binding_state = "suspended"

    @property
    def last_message_id(self) -> str:
        return getattr(self, "_last_message_id", "")

    async def run(self) -> None:
        self._last_message_id = ""
        if await self._try_reconnect_saved() and self.config.auto_bind_unique:
            await self._announce_auto_binding(self.terminal.session_metadata)
        if not self.terminal._attached.is_set() and self.config.auto_bind_unique:
            try:
                selected = await self._try_auto_bind_unique()
                if selected is not None:
                    await self._announce_auto_binding(selected)
            except Exception:
                self.binding_state = "suspended"
        output_task = asyncio.create_task(self.output_loop())
        poll_task = asyncio.create_task(self.message_poll_loop())
        reconnect_task = asyncio.create_task(self.binding_reconnect_loop())
        try:
            await self._run_ws()
        finally:
            output_task.cancel()
            poll_task.cancel()
            reconnect_task.cancel()
            await asyncio.gather(output_task, poll_task, reconnect_task,
                                 return_exceptions=True)
            if getattr(self, "_r5", None):
                self._r5.close()
            await self.terminal.close()

    async def _run_ws(self) -> None:
        # Zero means run until explicitly stopped; finite values remain useful
        # for tests and scheduled probes. Reconnect only this isolated sidecar.
        deadline = (None if self.config.listen_seconds <= 0 else
                    time.monotonic() + self.config.listen_seconds)
        while deadline is None or time.monotonic() < deadline:
            receive_task: asyncio.Task[Any] | None = None
            ping_task: asyncio.Task[Any] | None = None
            try:
                await self.ws_client._connect()  # SDK owns event ACK.
                receive_task = self.ws_client.receive_task
                await self._announce_owned_ready()
                ping_task = asyncio.create_task(self.ws_client._ping_loop())
                while deadline is None or time.monotonic() < deadline:
                    if receive_task.done():
                        await asyncio.gather(receive_task, return_exceptions=True)
                        break
                    remaining = 1.0 if deadline is None else max(
                        0.1, min(1.0, deadline - time.monotonic()))
                    await asyncio.sleep(remaining)
            except Exception:
                pass
            finally:
                for task in (receive_task, ping_task):
                    if task is not None:
                        task.cancel()
                await asyncio.gather(*(task for task in (receive_task, ping_task)
                                       if task is not None), return_exceptions=True)
                try:
                    await self.ws_client._disconnect()
                except Exception:
                    pass
            if deadline is not None and time.monotonic() >= deadline:
                break
            await asyncio.sleep(3)

    async def _announce_owned_ready(self) -> None:
        if not self.config.target_pane_id or getattr(self, "_owned_ready_announced", False):
            return
        if not self.terminal._attached.is_set() or (self._r5 and self._r5.busy):
            raise RuntimeError("owned_session_not_ready")
        # A connected UI means both transports and the user-visible receipt succeeded.
        text = ("CCPad 已接入飞书。\n你可以在这里继续桌面中的同一段对话，原有上下文保留。\n"
                "需要回到桌面时，在 Demo 的飞书菜单点击“切回桌面”。")
        await asyncio.to_thread(self.feishu.send_text, self.allowed_chat_id, text)
        self._owned_ready_announced = True
        write_owned_ready(self.config)


def parse_args(argv: list[str]) -> BridgeConfig:
    parser = argparse.ArgumentParser(description="CCPad single-window Feishu bridge demo")
    parser.add_argument("--credentials", type=Path, required=True)
    parser.add_argument("--web-base", default="http://127.0.0.1:9220")
    parser.add_argument("--web-token", default="")
    parser.add_argument("--web-descriptor", type=Path,
                        help="CCPad --feishu-demo descriptor; token stays in memory")
    parser.add_argument("--state-dir", type=Path, default=Path(".ccpad-feishu-demo"))
    parser.add_argument("--chat-id-file", type=Path,
                        help="local file containing {\"chat_id\": ...} for WS compensation")
    parser.add_argument("--allowed-chat-id", default="",
                        help="process events only from this Feishu chat")
    parser.add_argument("--listen-seconds", type=int, default=0,
                        help="run duration in seconds; zero keeps the Demo alive")
    parser.add_argument("--allow-input", action="store_true")
    parser.add_argument("--auto-bind-unique", action="store_true",
                        help="automatically bind the only visible CLI pane and announce it")
    parser.add_argument("--adopt-first-chat", action="store_true",
                        help="in an isolated pilot, lock to the first received chat when no allow-list is configured")
    parser.add_argument("--target-pane-id", default="")
    parser.add_argument("--window-id", default="")
    parser.add_argument("--generation", type=int, default=0)
    parser.add_argument("--expected-app-id", default="")
    parser.add_argument("--startup-ready", type=Path)
    args = parser.parse_args(argv)
    owned = any((args.target_pane_id, args.window_id, args.generation,
                 args.expected_app_id, args.startup_ready))
    if owned and (not all((args.target_pane_id, args.window_id, args.generation > 0,
                          args.expected_app_id, args.startup_ready, args.web_descriptor,
                          args.allowed_chat_id)) or args.auto_bind_unique or args.adopt_first_chat
                  or hashlib.sha256(args.expected_app_id.encode()).hexdigest() == RESERVED_APP_FINGERPRINT):
        raise ValueError("explicit dedicated App/chat/window/pane/generation required")
    web_base = args.web_base.rstrip("/")
    web_token = args.web_token
    web_pid: int | None = None
    if args.web_descriptor:
        descriptor = json.loads(args.web_descriptor.read_text(encoding="utf-8"))
        web_base = str(descriptor["base_url"]).rstrip("/")
        web_token = str(descriptor["token"])
        try:
            web_pid = int(descriptor.get("pid")) if descriptor.get("pid") is not None else None
        except (TypeError, ValueError):
            web_pid = None
    if args.listen_seconds < 0:
        raise ValueError("--listen-seconds must be zero or positive")
    config = BridgeConfig(args.credentials, web_base, web_token,
                          args.state_dir, args.listen_seconds, args.allow_input,
                          args.chat_id_file, web_pid, args.allowed_chat_id.strip() or None,
                          args.web_descriptor, args.auto_bind_unique, args.adopt_first_chat,
                          args.target_pane_id, args.window_id, args.generation,
                          args.expected_app_id, args.startup_ready)
    if owned:
        if web_pid is None or web_pid <= 0:
            raise ValueError("explicit descriptor process identity required")
        if descriptor.get("engine"):
            required = ("engine", "codex_home", "cli_session_id", "session_provenance")
            source = {key: str(descriptor.get(key) or "") for key in required}
            if (source["engine"] != "codex" or not Path(source["codex_home"]).is_absolute()
                    or source["session_provenance"] not in {"official_notify", "explicit_resume"}):
                raise ValueError("unsupported_public_source")
            uuid.UUID(source["cli_session_id"])
            config.public_source = source
        validate_owned_descriptor(config, descriptor)
    return config


def main(argv: list[str] | None = None) -> int:
    config = parse_args(argv or sys.argv[1:])
    logging.disable(logging.CRITICAL)
    try:
        credentials = read_credentials(config.credentials)
        if config.expected_app_id and credentials[0] != config.expected_app_id:
            raise RuntimeError("owned_app_identity_mismatch")
        owner = ReceiverOwnerLock(receiver_owner_path(credentials[0]))
        try:
            async def runner() -> None:
                bridge = BridgeDemo(config, credentials=credentials)
                if config.target_pane_id:
                    try:
                        await bridge.terminal.bind(config.target_pane_id)
                        if bridge.terminal._ack_version != 1:
                            raise RuntimeError("input_ack_unavailable")
                        bridge.binding_state = "bound"
                    except BaseException:
                        await bridge.terminal.close()
                        raise
                await bridge.run()

            asyncio.run(runner())
        finally:
            owner.close()
    except KeyboardInterrupt:
        return 130
    except Exception as exc:
        print(f"Demo failed: {type(exc).__name__}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
