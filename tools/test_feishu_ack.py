"""ACK safety boundaries with fake transport and temporary queue only."""
import asyncio
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import AsyncMock

import feishu_single_window_demo as demo
from offline_bridge_queue import OfflineBridgeQueue, OfflineQueueError


class FakeSocket:
    def __init__(self, mode):
        self.mode = mode
        self.frames = []
        self.incoming = asyncio.Queue()

    async def send(self, raw):
        frame = json.loads(raw)
        self.frames.append(frame)
        if self.mode == "missing":
            return
        ack = {"type": "input_ack", "inputId": frame["inputId"],
               "sessionId": frame["sessionId"], "status": "written", "ackScope": "pty_write"}
        if self.mode == "wrong":
            ack["inputId"] = "unrelated"
        if self.mode == "scope":
            ack["ackScope"] = "cli_completed"
        await self.incoming.put(json.dumps(ack))

    def __aiter__(self):
        return self

    async def __anext__(self):
        return await self.incoming.get()


class AckTests(unittest.IsolatedAsyncioTestCase):
    async def link(self, folder, mode, capable=True):
        config = demo.BridgeConfig(Path("fixture"), "http://127.0.0.1:1", "", Path(folder), 1, True)
        link = demo.TerminalLink(config)
        link.session_id = "pane"
        link.websocket = FakeSocket(mode)
        link._attached.set()
        link._ack_version = 1 if capable else 0
        link._max_input_bytes = 1048576
        link.ack_timeout = .02
        reader = asyncio.create_task(link._read_loop())
        self.addAsyncCleanup(self.stop_reader, reader)
        return link

    async def stop_reader(self, reader):
        reader.cancel()
        await asyncio.gather(reader, return_exceptions=True)

    def bridge(self, folder, link):
        bridge = demo.BridgeDemo.__new__(demo.BridgeDemo)
        bridge.terminal = link
        bridge.offline_queue = OfflineBridgeQueue(Path(folder) / "queue.json")
        bridge.offline_queue.enqueue("message", "chat", "text", text="fixture")
        return bridge

    async def test_correlated_pty_ack_removes_pending(self):
        with tempfile.TemporaryDirectory() as folder:
            link = await self.link(folder, "written")
            bridge = self.bridge(folder, link)
            expected = bridge.offline_queue.pending()[0]["input_id"]
            await bridge._deliver_queued("message", "fixture\r")
            self.assertEqual(bridge.offline_queue.pending(), [])
            self.assertEqual(link.websocket.frames[0]["inputId"], expected)

    async def test_legacy_server_rejected_without_input_and_keeps_queue(self):
        with tempfile.TemporaryDirectory() as folder:
            link = await self.link(folder, "written", capable=False)
            bridge = self.bridge(folder, link)
            with self.assertRaises(demo.InputRejected):
                await bridge._deliver_queued("message", "fixture\r")
            self.assertEqual(link.websocket.frames, [])
            self.assertEqual(bridge.offline_queue.pending()[0]["delivery_state"], "pending")

    async def test_missing_wrong_and_nonpty_ack_block_after_restart(self):
        for mode in ("missing", "wrong", "scope"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as folder:
                link = await self.link(folder, mode)
                bridge = self.bridge(folder, link)
                before = bridge.offline_queue.pending()[0]["input_id"]
                with self.assertRaises((TimeoutError, RuntimeError)):
                    await bridge._deliver_queued("message", "fixture\r")
                restored = OfflineBridgeQueue(Path(folder) / "queue.json")
                self.assertEqual(restored.pending()[0]["input_id"], before)
                self.assertEqual(restored.pending()[0]["delivery_state"], "delivery_unknown")
                bridge.offline_queue = restored
                link.send_input = AsyncMock()
                with self.assertRaises(OfflineQueueError):
                    await bridge._deliver_queued("message", "fixture\r")
                link.send_input.assert_not_awaited()

    async def test_wrong_queue_input_id_does_not_remove_pending(self):
        with tempfile.TemporaryDirectory() as folder:
            link = await self.link(folder, "written")
            bridge = self.bridge(folder, link)
            self.assertFalse(bridge.offline_queue.ack("message", "unrelated"))
            self.assertEqual(len(bridge.offline_queue.pending()), 1)

    async def test_unknown_fifo_head_blocks_new_message(self):
        with tempfile.TemporaryDirectory() as folder:
            link = await self.link(folder, "written")
            bridge = self.bridge(folder, link)
            bridge.offline_queue.begin_delivery("message")
            bridge.offline_queue.enqueue("later", "chat", "text", text="later")
            with self.assertRaises(OfflineQueueError):
                bridge.offline_queue.begin_delivery("later")
            self.assertEqual(link.websocket.frames, [])

    async def test_legacy_queue_without_delivery_proof_is_blocked(self):
        with tempfile.TemporaryDirectory() as folder:
            link = await self.link(folder, "written")
            bridge = self.bridge(folder, link)
            path = Path(folder) / "queue.json"
            data = json.loads(path.read_text())
            del data["items"][0]["input_id"]
            path.write_text(json.dumps(data))
            with self.assertRaises(OfflineQueueError):
                bridge.offline_queue.begin_delivery("message")
            self.assertEqual(link.websocket.frames, [])


if __name__ == "__main__":
    unittest.main()
