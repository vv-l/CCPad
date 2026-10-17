"""Single-window Feishu bridge demo for CCPad.

This pilot is deliberately a sidecar: it does not start a model, alter CCPad
state, or expose a public listener. CCPad's existing loopback WebTerminal
server remains the only terminal owner. The user explicitly binds one session
with ``/bridge bind <session-id>`` before ordinary Feishu text is forwarded.
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
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import quote, urlparse, urlunparse

import requests
import websockets
from lark_oapi import EventDispatcherHandler, LogLevel
from lark_oapi.ws import Client as FeishuWsClient


ANSI_RE = re.compile(r"\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1b\\))")
CREDS_RE = {
    "app_id": re.compile(r"^\s*App ID:\s*`([^`]+)`\s*$", re.MULTILINE),
    "app_secret": re.compile(r"^\s*App Secret:\s*`([^`]+)`\s*$", re.MULTILINE),
}


def read_credentials(path: Path) -> tuple[str, str]:
    """Read credentials in memory; never print the file or its values."""
    content = path.read_text(encoding="utf-8")
    values: dict[str, str] = {}
    for key, pattern in CREDS_RE.items():
        match = pattern.search(content)
        if not match:
            raise ValueError(f"missing {key} in credentials file")
        values[key] = match.group(1)
    return values["app_id"], values["app_secret"]


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


class TerminalLink:
    def __init__(self, config: BridgeConfig) -> None:
        self.config = config
        self.websocket: websockets.WebSocketClientProtocol | None = None
        self.session_id = ""
        self.session_metadata: dict[str, Any] = {}
        self._reader: asyncio.Task[None] | None = None
        self._output = bytearray()
        self._output_lock = asyncio.Lock()
        self._send_lock = asyncio.Lock()
        self._attached = asyncio.Event()
        self._stop = asyncio.Event()

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
        url = safe_url(self.config.web_base, "/api/sessions")
        params = {"token": self.config.web_token} if self.config.web_token else {}
        response = requests.get(url, params=params, timeout=15)
        if response.status_code != 200:
            raise RuntimeError(f"CCPad session list failed: http={response.status_code}")
        body = response.json()
        if not isinstance(body, list):
            raise RuntimeError("CCPad session list returned an unexpected response")
        return [x for x in body if isinstance(x, dict)]

    def _ws_url(self) -> str:
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
        if selected is None:
            sessions = await asyncio.to_thread(self.list_sessions)
            selected = next((x for x in sessions if x.get("id") == session_id), None)
        if selected is None:
            raise RuntimeError("session id was not found in CCPad")
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

    async def send_input(self, text: str) -> None:
        if not self.websocket or not self._attached.is_set():
            raise RuntimeError("no CCPad session is bound")
        async with self._send_lock:
            await self.websocket.send(json.dumps({"type": "input", "data": text}, ensure_ascii=False))

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
                    self._attached.set()
                elif kind in ("replay", "output"):
                    data = message.get("data") or ""
                    try:
                        chunk = base64.b64decode(data, validate=True)
                    except Exception:
                        continue
                    if kind == "output":
                        async with self._output_lock:
                            self._output.extend(chunk)
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
    def __init__(self, config: BridgeConfig) -> None:
        app_id, app_secret = read_credentials(config.credentials)
        self.config = config
        self.feishu = FeishuApi(app_id, app_secret)
        self.terminal = TerminalLink(config)
        self.allowed_chat_id = config.allowed_chat_id or self._load_allowed_chat_id()
        self.loop = asyncio.get_running_loop()
        self.started = time.monotonic()
        self.events = 0
        self.binding_state = "unbound"
        self._seen_message_hashes = self._load_seen_hashes()
        self.handler = (
            EventDispatcherHandler.builder("", "", level=LogLevel.ERROR)
            .register_p2_im_message_receive_v1(self._on_event)
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
            self._last_message_id = message_id
            self._seen_message_hashes.add(self._message_hash(message_id))
            self._save_seen_hashes()
            if self.allowed_chat_id and chat_id != self.allowed_chat_id:
                self._record_event_values(message_id, "ws", str(getattr(message, "chat_type", "")),
                                          message_type, "ignored_chat")
                return
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
                selected = await self.terminal.bind(session_id)
                self.binding_state = "active"
                label = selected.get("label") or session_id
                await self.reply(message_id, self._session_confirmation("已绑定 CCPad 单窗口", selected, label))
                return
            if text.startswith("/bridge resume "):
                cli_session_id = text[len("/bridge resume "):].strip()
                sessions = await asyncio.to_thread(self.terminal.list_sessions)
                candidates = [x for x in sessions
                              if str(x.get("sessionId") or "") == cli_session_id]
                if len(candidates) != 1:
                    await self.reply(message_id, "无法唯一恢复该 CLI Session；请先执行 /bridge sessions。")
                    return
                selected = candidates[0]
                await self.terminal.bind(str(selected.get("id") or ""), selected)
                self.binding_state = "reconnected"
                label = selected.get("label") or cli_session_id
                await self.reply(message_id, self._session_confirmation("已恢复 CCPad Session", selected, label))
                return
            if text == "/bridge unbind":
                await self.terminal.unbind()
                self.binding_state = "unbound"
                await self.reply(message_id, "已解除 CCPad 单窗口绑定。")
                return
            if text == "/bridge sessions":
                sessions = await asyncio.to_thread(self.terminal.list_sessions)
                if not sessions:
                    await self.reply(message_id, "CCPad 当前没有可见终端会话。")
                else:
                    lines = [self._session_line(x)
                             for x in sessions]
                    await self.reply(message_id, "可绑定会话：\n" + "\n".join(lines))
                return
            if text.startswith("/bridge"):
                await self.reply(message_id,
                                 "命令：/bridge status、/bridge whoami、/bridge sessions、"
                                 "/bridge bind <pane-id>、/bridge resume <cli-session-id>、/bridge unbind")
                return
            if not text:
                return
            if not self.config.allow_input:
                await self.reply(message_id, "Demo 当前是只读模式；启动时显式加入 --allow-input 后，绑定窗口才会接收普通消息。")
                return
            await self.terminal.send_input(text + "\r")
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
        if not self.terminal.session_id:
            await self.reply(message_id, "当前没有绑定 CCPad 窗口；请先执行 /bridge bind 或 /bridge resume。")
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
            await self.terminal.send_input(prompt + "\r")
            await self.reply(message_id, f"附件已转发到当前 CCPad 会话：{target.name}")
        except Exception as exc:
            await self.reply(message_id, f"附件处理失败：{type(exc).__name__}")

    async def status_text(self) -> str:
        try:
            sessions = await asyncio.to_thread(self.terminal.list_sessions)
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
            await asyncio.sleep(1.5)
            if not self.terminal._attached.is_set():
                continue
            output = await self.terminal.drain_output()
            if output:
                # Output notifications are intentionally low frequency; the
                # bridge never turns terminal deltas into model calls.
                try:
                    sessions = await asyncio.to_thread(self.terminal.list_sessions)
                    # A normal CCPad session has one p2p chat. Send is done by
                    # replying to the most recent event in run(); this loop's
                    # text is queued for the runner to attach to that chat.
                    if self.last_message_id:
                        await self.reply(self.last_message_id, "CCPad 输出：\n" + output)
                except Exception:
                    pass

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
                        self._seen_message_hashes.add(self._message_hash(message_id))
                        self._save_seen_hashes()
                        self._last_message_id = message_id
                        self.events += 1
                        text = str(parsed.get("text") or "")
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

    @property
    def last_message_id(self) -> str:
        return getattr(self, "_last_message_id", "")

    async def run(self) -> None:
        self._last_message_id = ""
        saved = self.terminal.load_binding_record()
        if saved:
            try:
                sessions = await asyncio.to_thread(self.terminal.list_sessions)
                selected = self.terminal.resolve_saved_session(sessions)
                if selected is None:
                    # Do not silently attach a new pane. The user can inspect
                    # /bridge sessions and explicitly choose /bridge resume.
                    self.binding_state = "orphaned"
                else:
                    await self.terminal.bind(str(selected.get("id") or ""), selected)
                    self.binding_state = "reconnected"
            except Exception:
                # A stopped CCPad server is a normal suspended condition. Keep
                # the binding file for a later explicit resume.
                self.binding_state = "suspended"
        output_task = asyncio.create_task(self.output_loop())
        poll_task = asyncio.create_task(self.message_poll_loop())
        try:
            await self._run_ws()
        finally:
            output_task.cancel()
            poll_task.cancel()
            await asyncio.gather(output_task, poll_task, return_exceptions=True)
            await self.terminal.close()

    async def _run_ws(self) -> None:
        await self.ws_client._connect()  # SDK connection; event ACK stays SDK-owned.
        receive_task = asyncio.create_task(self.ws_client._receive_message_loop())
        ping_task = asyncio.create_task(self.ws_client._ping_loop())
        try:
            deadline = time.monotonic() + self.config.listen_seconds
            while time.monotonic() < deadline:
                await asyncio.sleep(min(1.0, max(0.1, deadline - time.monotonic())))
        finally:
            receive_task.cancel()
            ping_task.cancel()
            await asyncio.gather(receive_task, return_exceptions=True)
            await self.ws_client._disconnect()
            await asyncio.gather(ping_task, return_exceptions=True)


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
    parser.add_argument("--listen-seconds", type=int, default=600)
    parser.add_argument("--allow-input", action="store_true")
    args = parser.parse_args(argv)
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
    return BridgeConfig(args.credentials, web_base, web_token,
                        args.state_dir, max(30, args.listen_seconds), args.allow_input,
                        args.chat_id_file, web_pid, args.allowed_chat_id.strip() or None)


def main(argv: list[str] | None = None) -> int:
    config = parse_args(argv or sys.argv[1:])
    logging.disable(logging.CRITICAL)
    try:
        async def runner() -> None:
            await BridgeDemo(config).run()

        asyncio.run(runner())
    except KeyboardInterrupt:
        return 130
    except Exception as exc:
        print(f"Demo failed: {type(exc).__name__}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
