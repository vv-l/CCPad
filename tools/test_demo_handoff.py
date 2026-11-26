import asyncio
import json
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, AsyncMock

import feishu_single_window_demo as demo


class DemoHandoffTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.bridge = object.__new__(demo.BridgeDemo)
        self.bridge.config = demo.BridgeConfig(root / 'credentials', 'http://127.0.0.1:1',
            'fixture-token', root, 0, True, target_pane_id='pane', window_id='window',
            generation=1, expected_app_id='cli_fixture', startup_ready=root / 'ready.json')
        self.bridge.allowed_chat_id = 'chat'
        attached = asyncio.Event()
        attached.set()
        self.bridge.terminal = SimpleNamespace(_attached=attached)
        self.bridge._r5 = SimpleNamespace(busy=False)
        self.bridge.feishu = SimpleNamespace(send_text=Mock())

    async def test_ready_requires_receipt_and_notice_is_not_repeated(self):
        await self.bridge._announce_owned_ready()
        ready = json.loads(self.bridge.config.startup_ready.read_text())
        self.assertEqual(ready['pane_id'], 'pane')
        self.assertNotIn('token', ready)
        await self.bridge._announce_owned_ready()
        self.bridge.feishu.send_text.assert_called_once()

    async def test_notice_failure_does_not_claim_ready(self):
        self.bridge.feishu.send_text.side_effect = OSError('fixture')
        with self.assertRaises(OSError):
            await self.bridge._announce_owned_ready()
        self.assertFalse(self.bridge.config.startup_ready.exists())

    async def test_notice_failure_still_closes_sdk_reader(self):
        bridge = self.bridge
        bridge.config.listen_seconds = 0.001
        bridge.feishu.send_text.side_effect = OSError('fixture')
        reader = asyncio.create_task(asyncio.Event().wait())
        async def connect():
            await asyncio.sleep(0.01)
        bridge.ws_client = SimpleNamespace(_connect=connect, receive_task=reader,
            _disconnect=AsyncMock(), _ping_loop=AsyncMock())
        await bridge._run_ws()
        self.assertTrue(reader.cancelled())
        bridge.ws_client._disconnect.assert_awaited_once()
        self.assertFalse(bridge.config.startup_ready.exists())

    async def test_unknown_task_or_detached_pane_cannot_claim_ready(self):
        self.bridge._r5.busy = True
        with self.assertRaises(RuntimeError):
            await self.bridge._announce_owned_ready()
        self.bridge._r5.busy = False
        self.bridge.terminal._attached.clear()
        with self.assertRaises(RuntimeError):
            await self.bridge._announce_owned_ready()
        self.bridge.feishu.send_text.assert_not_called()

    async def test_old_and_duplicate_ws_events_never_reach_session(self):
        bridge = self.bridge
        bridge.events = 0
        bridge.started_at_ms = 100
        bridge._seen_message_hashes = {bridge._message_hash('duplicate')}
        bridge._adopt_first_chat = Mock()
        bridge._queue_if_offline = Mock(return_value=False)
        bridge._mark_seen = lambda value: bridge._seen_message_hashes.add(bridge._message_hash(value))
        bridge._record_event = Mock()
        bridge.handle_text = AsyncMock()
        bridge.loop = asyncio.get_running_loop()
        for message_id, created in [('old', '99'), ('missing-time', '0'), ('duplicate', '101'), ('fresh', '101'), ('fresh', '101')]:
            message = SimpleNamespace(message_id=message_id, content='{"text":"fixture"}',
                message_type='text', chat_id='chat', create_time=created)
            bridge._on_event(SimpleNamespace(event=SimpleNamespace(message=message)))
        await asyncio.sleep(0)
        bridge.handle_text.assert_awaited_once_with('fresh', 'chat', 'fixture')


if __name__ == '__main__':
    unittest.main()
