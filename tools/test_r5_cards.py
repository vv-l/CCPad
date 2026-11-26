import hashlib
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace as NS
import unittest
from unittest.mock import patch

from r5_cards import TaskCards
from r5_cards.adapter import PINS


class FakeClient:
    def __init__(self, fail=False):
        self.calls = []
        self.fail = fail
        self.cardkit = NS(v1=NS(card=NS(create=self.create, settings=self.record),
                               card_element=NS(content=self.record, update=self.record)))
        self.im = NS(v1=NS(message=NS(create=self.message, reply=self.message)))

    def record(self, request):
        self.calls.append(request)
        return NS(success=lambda: True, code=0, msg="", data=NS())

    def create(self, request):
        self.calls.append(request)
        return NS(success=lambda: not self.fail, code=1 if self.fail else 0,
                  msg="fixture", data=NS(card_id="fixture-card"))

    def message(self, request):
        self.calls.append(request)
        return NS(success=lambda: True, code=0, msg="", data=NS(message_id="output-card-message"))


def task(**changes):
    result = dict(app_id="fixture-app", chat_id="chat", message_id="input-message",
                  task_id="fixture-task", input_id="input-id", window_id="window",
                  pane_id="pane", generation=2, thread_id="cli-thread", turn_id="cli-turn")
    return {**result, **changes}


def event(seq=0, kind="partial_text", **payload):
    return {**task(), "seq": seq, "type": kind,
            "payload": {"text": "public answer", "phase": "final_answer", **payload}}


class CardsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.env = patch.dict(os.environ, {"AUTOBOT_STATE_DIR": self.temp.name})
        self.env.start()
        self.addCleanup(self.env.stop)
        self.client = FakeClient()
        self.actions = []
        self.cards = TaskCards(self.client, can_abort=lambda binding: True,
                               task_action=lambda action, binding: self.actions.append((action, binding)) or True)
        self.addCleanup(self.cards.close)

    def test_vendor_pinned_original_layout_no_usage(self):
        vendor = Path(__file__).parent / "r5_cards" / "vendor"
        for name, digest in PINS.items():
            self.assertEqual(hashlib.sha256((vendor / name).read_bytes()).hexdigest(), digest)
        reporter = self.cards.create(task())
        self.assertEqual(reporter._fmt_usage(), "")
        card = reporter.streamer.__class__.__module__
        self.assertEqual(card, "r5_pinned_cardkit")
        self.assertEqual(reporter.streamer.reply_to, "input-message")
        initial = json.loads(self.client.calls[0].request_body.data)
        elements = initial["body"]["elements"]
        self.assertEqual([item.get("element_id") for item in elements[1:]],
                         ["tool_panel", "thinking_panel", "streaming_content", "footer"])
        self.assertEqual(elements[0]["columns"][1]["elements"][0]["value"],
                         {"action": "abort", "chat_id": "chat", "task_id": "fixture-task"})
        self.assertNotIn("feedback_form", str(initial))
        self.assertNotIn("advance_queue", str(initial))

    def test_create_idempotent_conflicting_binding_rejected(self):
        first = self.cards.create(task())
        count = len(self.client.calls)
        self.assertIs(self.cards.create(task()), first)
        self.assertEqual(len(self.client.calls), count)
        with self.assertRaises(ValueError):
            self.cards.create(task(pane_id="other"))
        with self.assertRaises(ValueError):
            self.cards.create(task(task_id="duplicate-input"))

    def test_all_binding_fields_guarded(self):
        self.cards.create(task())
        for field in task():
            wrong = event()
            wrong[field] = "wrong"
            self.assertFalse(self.cards.emit(wrong), field)

    def test_public_only_duplicates_and_final_once(self):
        reporter = self.cards.create(task())
        self.assertTrue(self.cards.emit(event()))
        self.assertFalse(reporter._finalized)
        self.assertFalse(self.cards.emit(event()))
        self.assertFalse(self.cards.emit(event(1, "thinking", phase="reasoning", text="private")))
        self.assertFalse(self.cards.emit(event(1, "tool", tool_input={"secret": "private"})))
        self.assertTrue(self.cards.emit(event(1, "terminal", status="success")))
        self.assertTrue(reporter._finalized)
        self.assertFalse(self.cards.emit(event(2)))
        self.assertFalse(self.cards.emit(event(2, "terminal", status="success")))

    def test_allowlist_copies_no_raw_payload(self):
        reporter = self.cards.create(task())
        seen = []
        reporter.on_event = seen.append
        self.assertTrue(self.cards.emit(event(raw_reasoning="private", tool_input="private", stdout="private")))
        self.assertEqual(set(seen[0]), {"type", "text", "completed", "item_id"})
        self.assertNotIn("private", str(seen))
        self.assertFalse(self.cards.emit(event(1, "process_message", phase="final_answer")))
        self.assertFalse(self.cards.emit(event(1, "partial_text", phase="commentary")))

    def test_commentary_not_final_answer(self):
        reporter = self.cards.create(task())
        self.assertTrue(self.cards.emit(event(0, "process_message", phase="commentary", text="public progress")))
        self.assertEqual(self.cards._tasks["fixture-task"]["final_text"], "")
        self.assertFalse(reporter._finalized)

    def test_abort_exact_output_message_not_input_and_ack_not_terminal(self):
        reporter = self.cards.create(task())
        self.assertFalse(self.cards.accepts_abort("fixture-task", "chat", "input-message"))
        self.assertFalse(self.cards.accepts_abort("fixture-task", "other", "output-card-message"))
        self.assertTrue(self.cards.abort("fixture-task", "chat", "output-card-message"))
        self.assertTrue(self.cards.matches_card("fixture-task", "chat", "output-card-message"))
        self.assertFalse(self.cards.accepts_abort("fixture-task", "chat", "output-card-message"))
        self.assertEqual(self.actions[0], ("abort", {**task(), "card_message_id": "output-card-message"}))
        self.assertFalse(self.cards.abort("fixture-task", "chat", "output-card-message"))
        self.assertEqual(len(self.actions), 1)
        self.assertFalse(reporter._finalized)
        self.assertTrue(self.cards.emit(event(0, "terminal", status="cancelled")))
        self.assertFalse(self.cards.accepts_abort("fixture-task", "chat", "output-card-message"))

    def test_callback_required_and_stale_guard(self):
        cards = TaskCards(self.client)
        with self.assertRaises(RuntimeError):
            cards.create(task())
        self.cards.create(task())
        self.cards.can_abort = lambda binding: False
        self.assertFalse(self.cards.abort("fixture-task", "chat", "output-card-message"))
        self.assertEqual(self.actions, [])

    def test_creation_failure_no_fallback_or_registration(self):
        cards = TaskCards(FakeClient(fail=True), can_abort=lambda task: True, task_action=lambda *args: True)
        with self.assertRaises(RuntimeError):
            cards.create(task())
        self.assertEqual(cards._tasks, {})

    def test_two_tasks_keep_original_message_and_turn_isolation(self):
        first = self.cards.create(task())
        second_task = task(task_id="second", input_id="second-input", message_id="second-message", turn_id="second-turn")
        second = self.cards.create(second_task)
        self.assertEqual(first.streamer.reply_to, "input-message")
        self.assertEqual(second.streamer.reply_to, "second-message")
        mixed = event()
        mixed["task_id"] = "second"
        self.assertFalse(self.cards.emit(mixed))
        self.assertTrue(self.cards.emit({**second_task, "seq": 0, "type": "partial_text",
                                        "payload": {"phase": "final_answer", "text": "second answer"}}))
        self.assertEqual(self.cards._tasks["fixture-task"]["final_text"], "")

    def test_close_stops_heartbeat_without_final_or_replay(self):
        reporter = self.cards.create(task())
        calls = len(self.client.calls)
        self.cards.close()
        self.assertTrue(reporter._heartbeat_stop.is_set())
        self.assertFalse(reporter._heartbeat_thread.is_alive())
        self.assertFalse(reporter._finalized)
        self.assertEqual(len(self.client.calls), calls)
        self.assertFalse(self.cards.emit(event()))
        with self.assertRaises(RuntimeError):
            self.cards.create(task(task_id="new"))

    def test_api_failure_blocks_future_writes_and_does_not_ack_public_event(self):
        reporter = self.cards.create(task())
        self.cards.client.cardkit.v1.card_element.content = self.cards.client._wrap(
            lambda request: NS(success=lambda: False, code=123, msg="private backend payload"))
        with self.assertRaisesRegex(RuntimeError, "ApiFailure"):
            self.cards.emit(event())
        self.assertEqual(self.cards._tasks["fixture-task"]["seq"], -1)
        self.assertTrue(reporter._heartbeat_stop.is_set())
        calls = len(self.client.calls)
        with self.assertRaises(RuntimeError):
            self.cards.emit(event(1, "terminal", status="success"))
        self.assertFalse(self.cards._tasks["fixture-task"]["terminal"])
        self.assertEqual(len(self.client.calls), calls)

    def test_terminal_api_exception_not_success_and_error_is_sanitized(self):
        self.cards.create(task())
        def fail(request):
            raise OSError("private token and payload")
        self.cards.client.cardkit.v1.card.settings = self.cards.client._wrap(fail)
        with self.assertRaises(RuntimeError) as caught:
            self.cards.emit(event(0, "terminal", status="success"))
        self.assertNotIn("private", str(caught.exception))
        self.assertFalse(self.cards._tasks["fixture-task"]["terminal"])
        self.assertEqual(self.cards._tasks["fixture-task"]["seq"], -1)


if __name__ == "__main__":
    unittest.main()
