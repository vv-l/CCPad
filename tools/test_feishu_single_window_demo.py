"""Offline checks for the single-window bridge demo.

These tests do not contact Feishu, CCPad, or a model service.
"""
from __future__ import annotations

import importlib.util
import os
import sys
import tempfile
import unittest
import asyncio
import json
from unittest.mock import patch
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("feishu_single_window_demo.py")
spec = importlib.util.spec_from_file_location("feishu_single_window_demo", MODULE_PATH)
assert spec and spec.loader
demo = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = demo
spec.loader.exec_module(demo)

from offline_bridge_queue import OfflineBridgeQueue
from session_index import SessionIndex


class SingleWindowDemoTests(unittest.TestCase):
    def test_session_index_records_stable_identity_without_duplicates(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            index = SessionIndex(Path(folder) / "session-index.json")
            session = {"id": "pane-1", "sessionId": "cli-1", "cliMode": "codex",
                       "workingDir": "D:/demo", "label": "Demo", "status": "waiting"}
            self.assertEqual(index.record([session]), 1)
            self.assertEqual(index.record([session]), 1)
            entries = index.entries()
            self.assertEqual(len(entries), 1)
            self.assertEqual(entries[0]["seen_count"], 2)
            self.assertEqual(index.find("cli-1")[0]["working_dir"], "D:/demo")

    def test_session_index_skips_sessions_without_stable_identity(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            index = SessionIndex(Path(folder) / "session-index.json")
            self.assertEqual(index.record([{"id": "pane-1", "label": "shell"}]), 0)
            self.assertEqual(index.entries(), [])

    def test_offline_queue_is_fifo_and_idempotent(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            queue = OfflineBridgeQueue(Path(folder) / "offline-message-queue.json")
            self.assertTrue(queue.enqueue("m-1", "chat-1", "text", text="first"))
            self.assertFalse(queue.enqueue("m-1", "chat-1", "text", text="duplicate"))
            self.assertTrue(queue.enqueue("m-2", "chat-1", "text", text="second"))
            self.assertEqual([item["text"] for item in queue.pending()], ["first", "second"])
            self.assertTrue(queue.ack("m-1", queue.begin_delivery("m-1")))
            self.assertEqual([item["text"] for item in queue.pending()], ["second"])

    def test_offline_queue_failure_is_durable(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "offline-message-queue.json"
            queue = OfflineBridgeQueue(path)
            queue.enqueue("m-1", "chat-1", "text", text="first")
            self.assertTrue(queue.mark_failed("m-1", "ConnectionError"))
            restored = OfflineBridgeQueue(path)
            item = restored.pending()[0]
            self.assertEqual(item["attempts"], 1)
            self.assertEqual(item["last_error"], "ConnectionError")

    def test_offline_queue_rejects_unsupported_kind(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            queue = OfflineBridgeQueue(Path(folder) / "offline-message-queue.json")
            with self.assertRaises(ValueError):
                queue.enqueue("m-1", "chat-1", "command", text="bad")

    def test_credentials_are_parsed_without_echoing_values(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "credentials.md"
            path.write_text("App ID: `cli_demo`\nApp Secret: `secret_demo`\n", encoding="utf-8")
            self.assertEqual(demo.read_credentials(path), ("cli_demo", "secret_demo"))

    def test_credentials_can_be_supplied_by_runtime_environment(self) -> None:
        with patch.dict(os.environ, {
            "CCPAD_FEISHU_APP_ID": "cli_runtime",
            "CCPAD_FEISHU_APP_SECRET": "secret_runtime",
        }, clear=False):
            self.assertEqual(demo.read_credentials(Path("missing-credentials.md")),
                             ("cli_runtime", "secret_runtime"))

    def test_terminal_output_cleaning_removes_control_sequences_and_limits_tail(self) -> None:
        value = demo.clean_terminal("\x1b[31mred\x1b[0m\r\n" + "x" * 3000)
        self.assertNotIn("\x1b", value)
        self.assertTrue(value.startswith("…"))
        self.assertLessEqual(len(value), 2601)

    def test_websocket_url_drops_http_query_and_keeps_only_token(self) -> None:
        config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220?old=bad", "t", Path("."), 30, False)
        self.assertEqual(demo.TerminalLink(config)._ws_url(), "ws://127.0.0.1:9220/ws?token=t")

    def test_descriptor_refresh_updates_isolated_endpoint(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            descriptor = Path(folder) / "feishu-demo-webterminal.json"
            descriptor.write_text(json.dumps({
                "base_url": "http://127.0.0.1:9231", "token": "new-token", "pid": 321,
            }), encoding="utf-8")
            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "old-token",
                                       Path(folder), 30, False, web_pid=123,
                                       web_descriptor=descriptor)
            link = demo.TerminalLink(config)
            self.assertTrue(link.refresh_descriptor())
            self.assertEqual(config.web_base, "http://127.0.0.1:9231")
            self.assertEqual(config.web_pid, 321)
            self.assertTrue(link.refresh_descriptor() is False)

    def test_binding_state_round_trip_is_local(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "", Path(folder), 30, False,
                                       web_pid=100)
            link = demo.TerminalLink(config)
            link.save_binding("pane-123", {
                "sessionId": "cli-123", "cliMode": "claude", "workingDir": "D:/demo",
                "label": "Demo pane",
            })
            self.assertEqual(link.load_binding(), "pane-123")
            record = link.load_binding_record()
            self.assertEqual(record["cli_session_id"], "cli-123")
            self.assertEqual(record["web_pid"], 100)
            link.clear_binding()
            self.assertEqual(link.load_binding(), "")

    def test_saved_binding_resolves_by_cli_session_after_web_pid_changes(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "", Path(folder), 30, False,
                                       web_pid=200)
            link = demo.TerminalLink(config)
            link.save_binding("old-pane", {
                "sessionId": "cli-456", "cliMode": "codex", "workingDir": "D:/project",
                "label": "Recovered",
            })
            current = [{
                "id": "new-pane", "sessionId": "cli-456", "cliMode": "codex",
                "workingDir": "D:/project", "status": "waiting",
            }]
            self.assertEqual(link.resolve_saved_session(current), current[0])

    def test_saved_binding_does_not_guess_when_stable_identity_differs(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "", Path(folder), 30, False,
                                       web_pid=200)
            link = demo.TerminalLink(config)
            link.save_binding("old-pane", {
                "sessionId": "cli-456", "cliMode": "codex", "workingDir": "D:/project",
            })
            current = [{
                "id": "new-pane", "sessionId": "cli-other", "cliMode": "codex",
                "workingDir": "D:/project",
            }]
            self.assertIsNone(link.resolve_saved_session(current))

    def test_session_line_contains_stable_identity_and_status(self) -> None:
        line = demo.BridgeDemo._session_line({
            "id": "pane-1", "label": "Demo", "sessionId": "cli-1",
            "cliMode": "claude", "status": "working",
        })
        self.assertIn("pane-1", line)
        self.assertIn("claude/working", line)
        self.assertIn("cli-1", line)

    def test_message_content_parses_text_image_and_file_without_body_storage(self) -> None:
        self.assertEqual(
            demo.parse_message_content("text", {"text": " hello "}),
            {"kind": "text", "text": "hello"},
        )
        self.assertEqual(
            demo.parse_message_content("image", {"image_key": "img-key"}),
            {"kind": "image", "resource_type": "image", "resource_key": "img-key"},
        )
        parsed = demo.parse_message_content("file", {"file_key": "file-key", "file_name": "a.txt"})
        self.assertEqual(parsed["resource_type"], "file")
        self.assertEqual(parsed["resource_key"], "file-key")
        self.assertEqual(parsed["file_name"], "a.txt")

    def test_attachment_name_is_local_and_bounded(self) -> None:
        value = demo.safe_attachment_name(r"C:\secret\my report?.png", "image.bin")
        self.assertEqual(value, "my_report_.png")
        self.assertNotIn("\\", value)
        self.assertLessEqual(len(value), 120)

    def test_message_resource_download_streams_with_cap(self) -> None:
        class Response:
            status_code = 200

            @staticmethod
            def iter_content(chunk_size: int):
                return [b"abc", b"123"]

        with tempfile.TemporaryDirectory() as folder:
            api = demo.FeishuApi("app", "secret")
            api._token = "tenant-token"
            api._token_deadline = 10**12
            response = Response()
            calls = []
            api._session.get = lambda *args, **kwargs: calls.append((args, kwargs)) or response
            target = Path(folder) / "attachments" / "a.bin"
            api.download_message_resource("om-msg", "file-key", "file", target)
            self.assertEqual(target.read_bytes(), b"abc123")
            self.assertIn("/messages/om-msg/resources/file-key", calls[0][0][0])
            self.assertEqual(calls[0][1]["params"], {"type": "file"})

    def test_feishu_reply_uses_text_payload(self) -> None:
        api = demo.FeishuApi("app", "secret")
        calls = []
        api._request = lambda method, path, **kwargs: calls.append((method, path, kwargs)) or {}
        api.reply("message-id", "状态正常")
        self.assertEqual(calls[0][0:2], ("POST", "/open-apis/im/v1/messages/message-id/reply"))
        self.assertEqual(calls[0][2]["json"]["msg_type"], "text")

    def test_feishu_send_text_targets_chat(self) -> None:
        api = demo.FeishuApi("app", "secret")
        calls = []
        api._request = lambda method, path, **kwargs: calls.append((method, path, kwargs)) or {}
        api.send_text("chat-1", "自动接入完成")
        self.assertEqual(calls[0][0:2], ("POST", "/open-apis/im/v1/messages"))
        self.assertEqual(calls[0][2]["params"], {"receive_id_type": "chat_id"})
        self.assertEqual(calls[0][2]["json"]["receive_id"], "chat-1")


class TerminalLinkProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def test_bind_and_input_use_one_attached_websocket(self) -> None:
        received: list[dict] = []
        ready = asyncio.Event()

        async def server(ws):
            attach = json.loads(await ws.recv())
            received.append(attach)
            await ws.send(json.dumps({"type": "attached", "sessionId": "pane-1",
                                      "inputAckVersion": 1, "maxInputMessageBytes": 1048576}))
            ready.set()
            frame = json.loads(await ws.recv())
            received.append(frame)
            await ws.send(json.dumps({"type": "input_ack", "inputId": frame["inputId"],
                                      "sessionId": "pane-1", "status": "written", "ackScope": "pty_write"}))

        websocket_server = await demo.websockets.serve(server, "127.0.0.1", 0)
        port = websocket_server.sockets[0].getsockname()[1]
        try:
            with tempfile.TemporaryDirectory() as folder:
                config = demo.BridgeConfig(Path("x"), f"http://127.0.0.1:{port}", "t",
                                            Path(folder), 30, True)
                link = demo.TerminalLink(config)
                link.list_sessions = lambda: [{"id": "pane-1", "label": "test"}]
                await link.bind("pane-1")
                await ready.wait()
                await link.send_input("hello\r", "fixture-input")
                for _ in range(20):
                    if len(received) == 2:
                        break
                    await asyncio.sleep(0.01)
                self.assertEqual(received, [
                    {"type": "attach", "sessionId": "pane-1"},
                    {"type": "input", "data": "hello\r", "inputId": "fixture-input", "sessionId": "pane-1"},
                ])
                await link.close()
        finally:
            websocket_server.close()
            await websocket_server.wait_closed()


class OfflineQueueDeliveryTests(unittest.IsolatedAsyncioTestCase):
    async def test_connect_accepts_pane_id_before_cli_thread_id_exists(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            class Terminal:
                session_id = ""

                def __init__(self) -> None:
                    self._attached = asyncio.Event()
                    self.bound: list[str] = []

                def list_sessions(self) -> list[dict]:
                    return [{"id": "pane-1", "sessionId": "", "cliMode": "codex",
                             "workingDir": "D:/demo", "status": "waiting"}]

                async def bind(self, pane_id: str, selected: dict) -> None:
                    self.bound.append(pane_id)
                    self.session_id = pane_id
                    self._attached.set()

            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "",
                                       Path(folder), 30, True)
            bridge = demo.BridgeDemo.__new__(demo.BridgeDemo)
            bridge.config = config
            bridge.terminal = Terminal()
            bridge.offline_queue = OfflineBridgeQueue(Path(folder) / "queue.json")
            bridge.session_index = SessionIndex(Path(folder) / "session-index.json")
            bridge._binding_lock = asyncio.Lock()
            bridge.binding_state = "unbound"
            replies: list[str] = []

            async def reply(message_id: str, text: str) -> None:
                replies.append(text)

            bridge.reply = reply
            await bridge.handle_text("msg-1", "chat-1", "/bridge connect pane-1")
            self.assertEqual(bridge.terminal.bound, ["pane-1"])
            self.assertIn("CCPad Bot 接入完成", replies[0])

    async def test_connect_binds_session_and_returns_metadata_summary(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            class Terminal:
                session_id = ""

                def __init__(self) -> None:
                    self._attached = asyncio.Event()
                    self.bound: list[str] = []

                def list_sessions(self) -> list[dict]:
                    return [{"id": "pane-1", "sessionId": "d47ac9",
                             "cliMode": "codex", "workingDir": "D:/demo",
                             "status": "waiting", "lastActivityUtc": "2026-10-10T15:00:00Z"}]

                async def bind(self, pane_id: str, selected: dict) -> None:
                    self.bound.append(pane_id)
                    self.session_id = pane_id
                    self._attached.set()

            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "",
                                       Path(folder), 30, True)
            bridge = demo.BridgeDemo.__new__(demo.BridgeDemo)
            bridge.config = config
            bridge.terminal = Terminal()
            bridge.offline_queue = OfflineBridgeQueue(Path(folder) / "queue.json")
            bridge.session_index = SessionIndex(Path(folder) / "session-index.json")
            bridge._binding_lock = asyncio.Lock()
            bridge.binding_state = "unbound"
            replies: list[tuple[str, str]] = []

            async def reply(message_id: str, text: str) -> None:
                replies.append((message_id, text))

            bridge.reply = reply
            await bridge.handle_text("msg-1", "chat-1", "/bridge connect d47ac9")
            self.assertEqual(bridge.terminal.bound, ["pane-1"])
            self.assertIn("CCPad Bot 接入完成", replies[0][1])
            self.assertIn("上下文：继续使用该 CCPad 原 CLI Session", replies[0][1])
            self.assertIn("图片/文件：可发送", replies[0][1])

    async def test_image_attachment_is_staged_and_injected_into_bound_session(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            class Terminal:
                session_id = "pane-1"

                def __init__(self) -> None:
                    self._attached = asyncio.Event()
                    self._attached.set()
                    self.sent: list[str] = []

                async def send_input(self, value: str, input_id: str) -> dict:
                    self.sent.append(value)
                    return {"inputId": input_id, "sessionId": self.session_id,
                            "status": "written", "ackScope": "pty_write"}

            class Feishu:
                def download_message_resource(self, message_id: str, resource_key: str,
                                              resource_type: str, target: Path) -> None:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(b"demo-image")

            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "",
                                       Path(folder), 30, True)
            bridge = demo.BridgeDemo.__new__(demo.BridgeDemo)
            bridge.config = config
            bridge.terminal = Terminal()
            bridge.feishu = Feishu()
            bridge.offline_queue = OfflineBridgeQueue(Path(folder) / "queue.json")
            replies: list[str] = []

            async def reply(message_id: str, text: str) -> None:
                replies.append(text)

            bridge.reply = reply
            await bridge.handle_attachment(
                "msg-image", "chat-1",
                {"kind": "image", "resource_type": "image",
                 "resource_key": "img-1", "file_name": "picture.png"},
            )
            self.assertEqual(len(bridge.terminal.sent), 1)
            self.assertIn("飞书image附件", bridge.terminal.sent[0])
            self.assertIn("picture.png", bridge.terminal.sent[0])
            self.assertIn("附件已转发到当前 CCPad 会话", replies[0])

    async def test_auto_bind_unique_cli_pane_and_announce_to_chat(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            class Terminal:
                session_id = ""

                def __init__(self) -> None:
                    self._attached = asyncio.Event()
                    self.bound: list[str] = []

                def list_sessions(self) -> list[dict]:
                    return [{"id": "pane-1", "sessionId": "cli-1",
                             "cliMode": "codex", "workingDir": "D:/demo",
                             "status": "waiting"}]

                async def bind(self, pane_id: str, selected: dict) -> None:
                    self.bound.append(pane_id)
                    self.session_id = pane_id
                    self._attached.set()

            class Feishu:
                def __init__(self) -> None:
                    self.sent: list[tuple[str, str]] = []

                def send_text(self, chat_id: str, text: str) -> None:
                    self.sent.append((chat_id, text))

            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "",
                                       Path(folder), 30, True,
                                       allowed_chat_id="chat-1", web_pid=42,
                                       auto_bind_unique=True)
            bridge = demo.BridgeDemo.__new__(demo.BridgeDemo)
            bridge.config = config
            bridge.terminal = Terminal()
            bridge.feishu = Feishu()
            bridge.allowed_chat_id = "chat-1"
            bridge.offline_queue = OfflineBridgeQueue(Path(folder) / "queue.json")
            bridge.session_index = SessionIndex(Path(folder) / "session-index.json")
            bridge._binding_lock = asyncio.Lock()
            bridge.binding_state = "unbound"
            bridge._auto_notice_key = ""
            selected = await bridge._try_auto_bind_unique()
            self.assertEqual(selected["id"], "pane-1")
            self.assertEqual(bridge.terminal.bound, ["pane-1"])
            await bridge._announce_auto_binding(selected)
            self.assertEqual(bridge.feishu.sent[0][0], "chat-1")
            self.assertIn("CCPad Bot 接入完成", bridge.feishu.sent[0][1])

    async def test_auto_bind_refuses_multiple_cli_panes(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            class Terminal:
                session_id = ""

                def __init__(self) -> None:
                    self._attached = asyncio.Event()
                    self.bound: list[str] = []

                def list_sessions(self) -> list[dict]:
                    return [{"id": "pane-a", "sessionId": "cli-a", "cliMode": "codex"},
                            {"id": "pane-b", "sessionId": "cli-b", "cliMode": "codex"}]

                async def bind(self, pane_id: str, selected: dict) -> None:
                    self.bound.append(pane_id)
                    self._attached.set()

            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "",
                                       Path(folder), 30, True, auto_bind_unique=True)
            bridge = demo.BridgeDemo.__new__(demo.BridgeDemo)
            bridge.config = config
            bridge.terminal = Terminal()
            bridge.session_index = SessionIndex(Path(folder) / "session-index.json")
            bridge._binding_lock = asyncio.Lock()
            bridge.binding_state = "unbound"
            selected = await bridge._try_auto_bind_unique()
            self.assertIsNone(selected)
            self.assertEqual(bridge.binding_state, "ambiguous")
            self.assertEqual(bridge.terminal.bound, [])

    async def test_isolated_pilot_can_adopt_first_chat(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "",
                                       Path(folder), 30, True,
                                       adopt_first_chat=True)
            bridge = demo.BridgeDemo.__new__(demo.BridgeDemo)
            bridge.config = config
            bridge.allowed_chat_id = ""
            bridge.terminal = type("Terminal", (), {
                "_attached": asyncio.Event(),
            })()
            self.assertTrue(bridge._adopt_first_chat("chat-first"))
            self.assertEqual(bridge.allowed_chat_id, "chat-first")
            self.assertFalse(bridge._adopt_first_chat("chat-second"))

    async def test_saved_binding_reconnects_only_to_unique_stable_session(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            class Terminal:
                session_id = ""

                def __init__(self) -> None:
                    self._attached = asyncio.Event()
                    self.saved = {
                        "pane_id": "old-pane", "cli_session_id": "cli-1",
                        "cli_mode": "codex", "working_dir": "D:/demo",
                    }
                    self.bound: list[str] = []

                def load_binding_record(self) -> dict:
                    return dict(self.saved)

                async def bind(self, pane_id: str, selected: dict) -> None:
                    self.bound.append(pane_id)
                    self.session_id = pane_id
                    self._attached.set()

                def list_sessions(self) -> list[dict]:
                    return [{"id": "new-pane", "sessionId": "cli-1",
                             "cliMode": "codex", "workingDir": "D:/demo"}]

                def resolve_saved_session(self, sessions: list[dict]) -> dict | None:
                    return sessions[0] if len(sessions) == 1 else None

            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "",
                                       Path(folder), 30, True)
            bridge = demo.BridgeDemo.__new__(demo.BridgeDemo)
            bridge.config = config
            bridge.terminal = Terminal()
            bridge.offline_queue = OfflineBridgeQueue(Path(folder) / "queue.json")
            bridge.session_index = SessionIndex(Path(folder) / "session-index.json")
            bridge._binding_lock = asyncio.Lock()
            bridge.binding_state = "suspended"
            self.assertTrue(await bridge._try_reconnect_saved())
            self.assertEqual(bridge.binding_state, "reconnected")
            self.assertEqual(bridge.terminal.bound, ["new-pane"])

    async def test_saved_binding_stays_orphaned_when_resolution_is_ambiguous(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            class Terminal:
                session_id = ""

                def __init__(self) -> None:
                    self._attached = asyncio.Event()
                    self.bound = False

                def load_binding_record(self) -> dict:
                    return {"pane_id": "old-pane", "cli_session_id": "cli-1"}

                def list_sessions(self) -> list[dict]:
                    return [{"id": "a", "sessionId": "cli-1"},
                            {"id": "b", "sessionId": "cli-1"}]

                def resolve_saved_session(self, sessions: list[dict]) -> None:
                    return None

                async def bind(self, pane_id: str, selected: dict) -> None:
                    self.bound = True

            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "",
                                       Path(folder), 30, True)
            bridge = demo.BridgeDemo.__new__(demo.BridgeDemo)
            bridge.config = config
            bridge.terminal = Terminal()
            bridge.offline_queue = OfflineBridgeQueue(Path(folder) / "queue.json")
            bridge.session_index = SessionIndex(Path(folder) / "session-index.json")
            bridge._binding_lock = asyncio.Lock()
            bridge.binding_state = "suspended"
            self.assertFalse(await bridge._try_reconnect_saved())
            self.assertEqual(bridge.binding_state, "orphaned")
            self.assertFalse(bridge.terminal.bound)

    async def test_flush_preserves_order_and_acks_after_send(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            queue = OfflineBridgeQueue(Path(folder) / "offline-message-queue.json")
            queue.enqueue("m-1", "chat-1", "text", text="first")
            queue.enqueue("m-2", "chat-1", "text", text="second")

            class Terminal:
                session_id = "pane-1"

                def __init__(self) -> None:
                    self._attached = asyncio.Event()
                    self._attached.set()
                    self.sent: list[str] = []

                async def send_input(self, value: str, input_id: str) -> dict:
                    self.sent.append(value)
                    return {"inputId": input_id, "sessionId": self.session_id,
                            "status": "written", "ackScope": "pty_write"}

            config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220", "",
                                       Path(folder), 30, True)
            bridge = demo.BridgeDemo.__new__(demo.BridgeDemo)
            bridge.config = config
            bridge.terminal = Terminal()
            bridge.offline_queue = queue
            replies: list[tuple[str, str]] = []

            async def reply(message_id: str, text: str) -> None:
                replies.append((message_id, text))

            bridge.reply = reply
            self.assertEqual(await bridge.flush_offline_queue(), 2)
            self.assertEqual(bridge.terminal.sent, ["first\r", "second\r"])
            self.assertEqual(queue.pending(), [])
            self.assertEqual([item[0] for item in replies], ["m-1", "m-2"])


if __name__ == "__main__":
    unittest.main()
