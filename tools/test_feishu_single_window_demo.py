"""Offline checks for the single-window bridge demo.

These tests do not contact Feishu, CCPad, or a model service.
"""
from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
import asyncio
import json
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("feishu_single_window_demo.py")
spec = importlib.util.spec_from_file_location("feishu_single_window_demo", MODULE_PATH)
assert spec and spec.loader
demo = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = demo
spec.loader.exec_module(demo)


class SingleWindowDemoTests(unittest.TestCase):
    def test_credentials_are_parsed_without_echoing_values(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "credentials.md"
            path.write_text("App ID: `cli_demo`\nApp Secret: `secret_demo`\n", encoding="utf-8")
            self.assertEqual(demo.read_credentials(path), ("cli_demo", "secret_demo"))

    def test_terminal_output_cleaning_removes_control_sequences_and_limits_tail(self) -> None:
        value = demo.clean_terminal("\x1b[31mred\x1b[0m\r\n" + "x" * 3000)
        self.assertNotIn("\x1b", value)
        self.assertTrue(value.startswith("…"))
        self.assertLessEqual(len(value), 2601)

    def test_websocket_url_drops_http_query_and_keeps_only_token(self) -> None:
        config = demo.BridgeConfig(Path("x"), "http://127.0.0.1:9220?old=bad", "t", Path("."), 30, False)
        self.assertEqual(demo.TerminalLink(config)._ws_url(), "ws://127.0.0.1:9220/ws?token=t")

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


class TerminalLinkProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def test_bind_and_input_use_one_attached_websocket(self) -> None:
        received: list[dict] = []
        ready = asyncio.Event()

        async def server(ws):
            attach = json.loads(await ws.recv())
            received.append(attach)
            await ws.send(json.dumps({"type": "attached", "sessionId": "pane-1"}))
            ready.set()
            received.append(json.loads(await ws.recv()))

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
                await link.send_input("hello\r")
                for _ in range(20):
                    if len(received) == 2:
                        break
                    await asyncio.sleep(0.01)
                self.assertEqual(received, [
                    {"type": "attach", "sessionId": "pane-1"},
                    {"type": "input", "data": "hello\r"},
                ])
                await link.close()
        finally:
            websocket_server.close()
            await websocket_server.wait_closed()


if __name__ == "__main__":
    unittest.main()
