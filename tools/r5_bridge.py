"""Bind one acknowledged CCPad input and exact rollout turn to its old card."""
from __future__ import annotations

import asyncio
import json
import os
from pathlib import Path
import uuid

from r5_rollout import RolloutSource


class PublicTaskBridge:
    def __init__(self, bridge, *, source=None, cards=None):
        self.bridge = bridge
        self.config = bridge.config
        identity = self.config.public_source
        self.source = source or RolloutSource(identity["codex_home"], identity["cli_session_id"])
        self._lock = asyncio.Lock()
        self.active = None
        self.blocked_reason = ""
        self._card_started = False
        self._abort_requested = False
        self._pending_events = []
        self._last_seq = 0
        self.journal = self.config.state_dir / "r5-active-task.json"
        if self.journal.exists():
            previous = json.loads(self.journal.read_text(encoding="utf-8"))
            if previous.get("state") != "completed":
                self.blocked_reason = "previous_task_requires_review"
        if any(item.get("delivery_state") == "delivery_unknown"
               for item in bridge.offline_queue.pending()):
            self.blocked_reason = "previous_delivery_unknown"
        if cards is None:
            # The vendored old renderer writes its existing private task records here.
            os.environ["AUTOBOT_STATE_DIR"] = str(self.config.state_dir / "r5-card-state")
            import lark_oapi as lark
            from r5_cards import TaskCards
            client = (lark.Client.builder().app_id(bridge.feishu.app_id)
                      .app_secret(bridge.feishu.app_secret).build())
            cards = TaskCards(client, can_abort=lambda task: bool(
                self.active and not self.blocked_reason and
                not self._abort_requested and task["task_id"] == self.active["task_id"]),
                task_action=self._schedule_abort)
        self.cards = cards

    def _schedule_abort(self, action, task):
        if action != "abort":
            return False
        self.bridge.loop.call_soon_threadsafe(lambda: asyncio.create_task(self.abort(
            task["task_id"], task["chat_id"], task["card_message_id"])))
        return True

    @property
    def busy(self):
        return bool(self.active or self.blocked_reason or self.source.busy)

    def _record(self, state):
        self.config.state_dir.mkdir(parents=True, exist_ok=True)
        record = {key: value for key, value in (self.active or {}).items() if key != "prompt"}
        record.update(state=state, reason=self.blocked_reason)
        staging = self.journal.with_suffix(".tmp")
        staging.write_text(json.dumps(record, ensure_ascii=False), encoding="utf-8")
        os.replace(staging, self.journal)

    def _block(self, reason):
        self.blocked_reason = reason
        self._record("blocked")
        self.cards.close()

    async def _check_identity(self):
        terminal = self.bridge.terminal
        terminal.refresh_descriptor()
        if (not terminal._attached.is_set()
                or terminal.session_id != self.config.target_pane_id):
            raise RuntimeError("public_target_detached")
        sessions = await asyncio.to_thread(terminal.list_sessions)
        matches = [item for item in sessions if item.get("id") == self.config.target_pane_id]
        identity = self.config.public_source
        if (len(matches) != 1 or matches[0].get("sessionId") != identity["cli_session_id"]
                or str(matches[0].get("cliMode", "")).lower() != "codex"
                or matches[0].get("sessionProvenance") != identity["session_provenance"]):
            raise RuntimeError("public_target_identity_changed")

    async def deliver(self, message_id, text):
        async with self._lock:
            if self.busy:
                raise RuntimeError("public_task_unresolved")
            text = text.replace("\r\n", "\n").replace("\r", "\n")
            if any(ord(char) < 32 and char not in "\n\t" for char in text):
                raise ValueError("terminal_control_in_prompt")
            await self._check_identity()
            pending = self.bridge.offline_queue.pending()
            if (not pending or pending[0].get("message_id") != message_id
                    or pending[0].get("chat_id") != self.bridge.allowed_chat_id
                    or pending[0].get("delivery_state") != "pending"):
                raise RuntimeError("public_input_not_pending_head")
            item = pending[0]
            task_id = uuid.uuid4().hex
            input_id = item["input_id"]
            wire = f"[CCPad bridge task: {task_id}]\n{text}"
            self.source.arm(task_id, input_id, wire)
            self.active = dict(app_id=self.config.expected_app_id,
                               chat_id=item["chat_id"], message_id=message_id,
                               task_id=task_id, input_id=input_id,
                               window_id=self.config.window_id, pane_id=self.config.target_pane_id,
                               generation=self.config.generation,
                               thread_id=self.config.public_source["cli_session_id"],
                               turn_id="", prompt=text)
            self._card_started = False
            self._abort_requested = False
            self._last_seq = 0
            self._record("armed")
            self.bridge.offline_queue.begin_delivery(message_id)
            try:
                # Bracketed paste keeps the marker and multiline prompt in one CLI submission.
                ack = await self.bridge.terminal.send_input("\x1b[200~" + wire + "\x1b[201~\r", input_id)
            except Exception as error:
                from feishu_single_window_demo import InputRejected
                if isinstance(error, InputRejected):
                    self.source.confirm_ack(input_id, "rejected")
                    self.bridge.offline_queue.mark_rejected(message_id)
                    self._record("completed")
                    self.active = None
                else:
                    self.source.confirm_ack(input_id, "unknown")
                    self._block("input_delivery_unknown")
                raise
            if (not isinstance(ack, dict) or ack.get("inputId") != input_id
                    or ack.get("sessionId") != self.config.target_pane_id
                    or ack.get("status") != "written" or ack.get("ackScope") != "pty_write"):
                self.source.confirm_ack(input_id, "unknown")
                self._block("input_delivery_unknown")
                raise RuntimeError("input_delivery_unknown")
            self.source.confirm_ack(input_id, "written")
            self._record("awaiting_turn")
            # Keep delivery_unknown in the durable FIFO until explicit model completion.

    async def poll(self):
        completed = False
        async with self._lock:
            if not self.active or self.blocked_reason:
                return
            try:
                await self._check_identity()
                events = self._pending_events + self.source.poll()
                self._pending_events = []
                if self.source.blocked:
                    self._block(self.source.blocked_reason)
                    return
                self._validate_events(events)
                for event in events:
                    for key in ("task_id", "input_id", "thread_id"):
                        if event.get(key) != self.active[key]:
                            raise RuntimeError("public_event_identity_mismatch")
                    turn_id = event.get("turn_id")
                    if not turn_id or (self.active["turn_id"] and turn_id != self.active["turn_id"]):
                        raise RuntimeError("public_turn_identity_mismatch")
                    self.active["turn_id"] = turn_id
                    if not self._card_started:
                        await asyncio.to_thread(self.cards.create, dict(self.active))
                        self._card_started = True
                    envelope = {key: value for key, value in self.active.items() if key != "prompt"}
                    envelope.update(event)
                    accepted = await asyncio.to_thread(self.cards.emit, envelope)
                    if not accepted:
                        raise RuntimeError("public_card_event_rejected")
                    self._last_seq = event["seq"]
                    if event["type"] == "terminal":
                        if not self.source.terminal:
                            raise RuntimeError("public_terminal_unproven")
                        if not self.bridge.offline_queue.ack(self.active["message_id"], self.active["input_id"]):
                            raise RuntimeError("public_completed_queue_mismatch")
                        self._record("completed")
                        self.active = None
                        completed = True
                        break
                if self.active:
                    self._record("running" if self.active["turn_id"] else "awaiting_turn")
            except Exception as error:
                # A transport error can mean the card was already written. Do not resend it.
                self._block("public_output_" + type(error).__name__)
        if completed:
            await self.bridge.flush_offline_queue()

    def _validate_events(self, events):
        turn_id, seq = self.active["turn_id"], self._last_seq
        keys = {"task_id", "input_id", "thread_id", "turn_id", "seq", "type", "payload"}
        for index, event in enumerate(events):
            if not isinstance(event, dict) or set(event) != keys:
                raise RuntimeError("public_event_shape_invalid")
            if any(event[key] != self.active[key] for key in ("task_id", "input_id", "thread_id")):
                raise RuntimeError("public_event_identity_mismatch")
            if not event["turn_id"] or (turn_id and turn_id != event["turn_id"]):
                raise RuntimeError("public_turn_identity_mismatch")
            turn_id = event["turn_id"]
            if type(event["seq"]) is not int or event["seq"] != seq + 1:
                raise RuntimeError("public_sequence_invalid")
            seq = event["seq"]
            payload = event["payload"]
            if not isinstance(payload, dict):
                raise RuntimeError("public_payload_invalid")
            if event["type"] in ("partial_text", "process_message"):
                phase = "final_answer" if event["type"] == "partial_text" else "commentary"
                if (set(payload) != {"text", "phase"} or payload["phase"] != phase
                        or not isinstance(payload["text"], str)):
                    raise RuntimeError("public_payload_invalid")
            elif event["type"] == "terminal":
                if (index != len(events) - 1 or not self.source.terminal
                        or set(payload) != {"status"}
                        or payload["status"] not in {"success", "cancelled"}):
                    raise RuntimeError("public_terminal_unproven")
            else:
                raise RuntimeError("public_event_type_invalid")

    async def abort(self, task_id, chat_id, card_message_id):
        async with self._lock:
            if (self.blocked_reason or not self.active or self._abort_requested
                    or not self.active["turn_id"] or chat_id != self.bridge.allowed_chat_id
                    or task_id != self.active["task_id"]
                    or not self.cards.matches_card(task_id, chat_id, card_message_id)):
                return False
            try:
                await self._check_identity()
                # A completed or interleaved turn must not be interrupted.
                self._pending_events.extend(self.source.poll())
                self._validate_events(self._pending_events)
            except Exception as error:
                self._block("abort_identity_" + type(error).__name__)
                return False
            if self.source.blocked:
                self._block(self.source.blocked_reason)
                return False
            if self.source.terminal:
                return False
            self._abort_requested = True
            self._record("abort_requested")
            try:
                input_id = uuid.uuid4().hex
                ack = await self.bridge.terminal.send_input(
                    "\x03", input_id, expected_turn_id=self.active["turn_id"])
                if (not isinstance(ack, dict) or ack.get("status") != "written"
                        or ack.get("ackScope") != "pty_write" or ack.get("inputId") != input_id
                        or ack.get("sessionId") != self.config.target_pane_id):
                    raise RuntimeError("abort_delivery_unknown")
            except Exception:
                self._block("abort_delivery_unknown")
                return False
            return True  # Finalize only after the matching turn_aborted record.

    def close(self):
        self.cards.close()
