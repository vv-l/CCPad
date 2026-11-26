"""Real source, bridge and pinned renderer with a fake Feishu SDK boundary."""
import asyncio
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace as NS
import unittest
from unittest.mock import AsyncMock, Mock, patch

from feishu_single_window_demo import BridgeConfig, BridgeDemo, write_owned_ready
from offline_bridge_queue import OfflineBridgeQueue
from r5_bridge import PublicTaskBridge
from r5_cards import TaskCards
from test_r5_cards import FakeClient
from test_r5_rollout import SESSION, assistant, event, user


class EndToEndTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.env = patch.dict(os.environ, {"AUTOBOT_STATE_DIR": str(self.root / "cards")})
        self.env.start()
        self.addCleanup(self.env.stop)
        (self.root / "sessions").mkdir()
        self.path = self.root / "sessions" / f"rollout-fixture-{SESSION}.jsonl"
        self.append({"type": "session_meta", "payload": {"id": SESSION}},
                    event("task_started", turn_id="previous"), user("PRIVATE_OLD"),
                    event("task_complete", turn_id="previous"))
        self.config = BridgeConfig(Path("unused"), "http://127.0.0.1:1", "fixture-token",
            self.root / "state", 1, True, target_pane_id="pane", window_id="window",
            generation=7, expected_app_id="app", startup_ready=self.root / "ready.json",
            public_source=dict(engine="codex", codex_home=str(self.root), cli_session_id=SESSION,
                               session_provenance="official_notify"))
        self.frames = []
        self.abort_written = asyncio.Event()
        attached = asyncio.Event(); attached.set()
        async def send(text, input_id, **kwargs):
            self.frames.append((text, input_id, kwargs))
            if text == "\x03":
                self.abort_written.set()
            return dict(status="written", ackScope="pty_write", inputId=input_id, sessionId="pane")
        self.bridge = NS(config=self.config, allowed_chat_id="chat", loop=asyncio.get_running_loop(),
            terminal=NS(session_id="pane", _attached=attached, refresh_descriptor=lambda: None,
                list_sessions=lambda: [dict(id="pane", sessionId=SESSION, cliMode="codex",
                                             sessionProvenance="official_notify")], send_input=send),
            offline_queue=OfflineBridgeQueue(self.root / "queue.json"), flush_offline_queue=AsyncMock())
        self.bridge.offline_queue.enqueue("input-message", "chat", "text", text="public prompt")
        self.client = FakeClient()
        self.client.fail_settings = False
        self.client.cardkit.v1.card.settings = lambda request: NS(
            success=lambda: not self.client.fail_settings, code=1, msg="fixture", data=NS())
        self.cards = TaskCards(self.client, can_abort=lambda _: True, task_action=lambda *_: False)
        self.addCleanup(self.cards.close)
        self.public = PublicTaskBridge(self.bridge, cards=self.cards)
        self.cards.task_action = Mock(wraps=self.public._schedule_abort)
        self.bridge._r5 = self.public

    def append(self, *records):
        with self.path.open("ab") as output:
            for record in records:
                output.write((json.dumps(record) + "\n").encode())

    async def begin(self):
        await self.public.deliver("input-message", "public prompt")
        wire = self.frames[0][0].removeprefix("\x1b[200~").removesuffix("\x1b[201~\r")
        self.append(event("task_started", turn_id="this-turn"), user(wire),
                    assistant("public progress", "commentary"))
        await self.public.poll()

    async def test_source_to_same_old_card_and_explicit_terminal(self):
        await self.begin()
        self.assertFalse(self.public.blocked_reason)
        self.assertEqual(self.bridge.offline_queue.pending()[0]["delivery_state"], "delivery_unknown")
        self.append({"type": "response_item", "payload": {"type": "reasoning", "content": "PRIVATE_SECRET"}},
                    assistant("public answer"), event("task_complete", turn_id="this-turn"))
        await self.public.poll()
        self.assertFalse(self.public.blocked_reason)
        self.assertEqual(self.bridge.offline_queue.pending(), [])
        self.bridge.flush_offline_queue.assert_awaited_once()
        state = next(iter(self.cards._tasks.values()))
        self.assertTrue(state["terminal"])
        self.assertEqual(state["reporter"].streamer.streaming_text, "public answer")
        self.assertEqual(state["reporter"].streamer.reply_to, "input-message")
        self.assertNotIn("PRIVATE_SECRET", json.dumps(state["reporter"].streamer.progress_entries))

    async def test_old_abort_callback_reaches_guarded_input_only_once(self):
        await self.begin()
        task_id = self.public.active["task_id"]
        data = NS(event=NS(action=NS(value=dict(action="abort", task_id=task_id, chat_id="chat")),
                           context=NS(open_chat_id="chat", open_message_id="output-card-message")))
        BridgeDemo._on_card_action(self.bridge, data)
        await asyncio.wait_for(self.abort_written.wait(), timeout=5)
        self.assertEqual(self.frames[-1][0], "\x03")
        self.assertEqual(self.frames[-1][2], {"expected_turn_id": "this-turn"})
        BridgeDemo._on_card_action(self.bridge, data)
        self.cards.task_action.assert_called_once()
        self.assertEqual(len(self.frames), 2)
        self.assertEqual(len(self.bridge.offline_queue.pending()), 1)
        self.append(event("turn_aborted", turn_id="this-turn"))
        await self.public.poll()
        self.assertEqual(self.bridge.offline_queue.pending(), [])

    async def test_card_final_api_failure_retains_unknown_queue(self):
        await self.begin()
        self.client.fail_settings = True
        self.append(assistant("public answer"), event("task_complete", turn_id="this-turn"))
        await self.public.poll()
        self.assertTrue(self.public.blocked_reason)
        self.assertEqual(self.bridge.offline_queue.pending()[0]["delivery_state"], "delivery_unknown")
        self.bridge.flush_offline_queue.assert_not_awaited()

    async def test_owned_ready_has_exact_source_capability_without_private_home(self):
        write_owned_ready(self.config)
        ready = json.loads(self.config.startup_ready.read_text())
        self.assertEqual(ready["public_source"], "codex_rollout_v1")
        self.assertEqual(ready["cli_session_id"], SESSION)
        self.assertEqual(ready["ack_scope"], "pty_write")
        self.assertNotIn("codex_home", ready)
        self.assertNotIn("token", ready)


if __name__ == "__main__":
    unittest.main()
