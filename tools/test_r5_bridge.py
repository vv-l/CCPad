"""Real rollout/queue with fake terminal/cards; no receiver, CLI or network."""
import asyncio
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest

from offline_bridge_queue import OfflineBridgeQueue
from r5_bridge import PublicTaskBridge
from r5_rollout import RolloutSource


SESSION = '11111111-1111-4111-8111-111111111111'


def event(kind, **values):
    return {'type': 'event_msg', 'payload': {'type': kind, **values}}


def message(role, text, phase=None):
    return {'type': 'response_item', 'payload': {'type': 'message', 'role': role, 'phase': phase,
        'content': [{'type': 'input_text' if role == 'user' else 'output_text', 'text': text}]}}


class FakeTerminal:
    def __init__(self):
        self.session_id = 'pane'
        self._attached = asyncio.Event(); self._attached.set()
        self.session = SESSION
        self.generation = 3
        self.calls = []
        self.ack_mode = 'written'
        self.abort_ack_mode = 'written'

    def refresh_descriptor(self):
        if self.generation != 3:
            raise RuntimeError('owned_descriptor_identity_mismatch')

    def list_sessions(self):
        return [{'id': 'pane', 'sessionId': self.session, 'cliMode': 'codex',
                 'sessionProvenance': 'official_notify'}]

    async def send_input(self, text, input_id, **kwargs):
        self.calls.append((text, input_id, kwargs))
        mode = self.abort_ack_mode if text == '\x03' else self.ack_mode
        if mode == 'rejected':
            from feishu_single_window_demo import InputRejected
            raise InputRejected('cli_identity_mismatch')
        return {'inputId': 'wrong' if mode == 'wrong-id' else input_id,
                'sessionId': 'pane', 'status': 'unknown' if mode == 'unknown' else 'written',
                'ackScope': 'pty_write'}


class FakeCards:
    def __init__(self):
        self.created, self.emitted = [], []
        self.closed = 0
        self.fail = ''

    def create(self, task):
        if self.fail == 'create':
            raise RuntimeError('fixture creation uncertainty')
        self.created.append(task)

    def emit(self, envelope):
        if self.fail == 'emit' or (self.fail == 'terminal' and envelope['type'] == 'terminal'):
            return False
        self.emitted.append(envelope)
        return True

    def matches_card(self, task_id, chat_id, card_message_id):
        return bool(self.created and self.created[-1]['task_id'] == task_id and
                    chat_id == 'chat' and card_message_id == 'real-card')

    def close(self):
        self.closed += 1


class BridgeIntegrationTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        if hasattr(self, 'temp'):
            self.temp.cleanup()
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.home = self.root / 'codex'; (self.home / 'sessions').mkdir(parents=True)
        self.path = self.home / 'sessions' / ('rollout-fixture-' + SESSION + '.jsonl')
        self.path.write_text('')
        self.append({'type': 'session_meta', 'payload': {'id': SESSION}},
                    event('task_started', turn_id='old'), event('task_complete', turn_id='old'))
        self.state = self.root / 'state'
        self.queue = OfflineBridgeQueue(self.state / 'queue.json')
        self.terminal = FakeTerminal(); self.cards = FakeCards()
        config = SimpleNamespace(state_dir=self.state, expected_app_id='fixture-app',
            target_pane_id='pane', window_id='window', generation=3,
            public_source={'codex_home': str(self.home), 'cli_session_id': SESSION,
                           'session_provenance': 'official_notify'})
        self.bridge = SimpleNamespace(config=config, terminal=self.terminal,
            allowed_chat_id='chat', offline_queue=self.queue, loop=None)
        self.flushes = 0
        async def flush():
            self.flushes += 1
            pending = self.queue.pending()
            if pending and not self.engine.busy:
                await self.engine.deliver(pending[0]['message_id'], pending[0]['text'])
        self.bridge.flush_offline_queue = flush
        self.source = RolloutSource(self.home, SESSION)
        self.engine = PublicTaskBridge(self.bridge, source=self.source, cards=self.cards)

    def tearDown(self):
        self.temp.cleanup()

    def append(self, *records):
        with self.path.open('a', encoding='utf-8') as stream:
            for record in records: stream.write(json.dumps(record) + '\n')

    def enqueue(self, name='message-1', text='user text'):
        self.queue.enqueue(name, 'chat', 'text', text=text)

    def wire(self):
        raw = self.terminal.calls[-1][0]
        self.assertTrue(raw.startswith('\x1b[200~') and raw.endswith('\x1b[201~\r'))
        return raw[len('\x1b[200~'):-len('\x1b[201~\r')]

    def start_log(self, turn='turn', public='public'):
        self.append(event('task_started', turn_id=turn), message('user', self.wire()),
                    message('assistant', public, 'commentary'))

    async def submit(self):
        self.enqueue(); await self.engine.deliver('message-1', 'user text')

    async def test_ack_keeps_unknown_until_correlated_terminal_and_card(self):
        await self.submit()
        self.assertIn('[CCPad bridge task: ' + self.engine.active['task_id'] + ']\nuser text', self.wire())
        self.assertEqual(self.queue.pending()[0]['delivery_state'], 'delivery_unknown')
        self.start_log(); await self.engine.poll()
        self.assertEqual(len(self.cards.created), 1)
        self.assertEqual(len(self.queue.pending()), 1)
        self.assertEqual(self.cards.emitted[0]['window_id'], 'window')
        self.assertEqual(self.cards.emitted[0]['generation'], 3)
        self.append(message('assistant', 'final', 'final_answer'), event('task_complete', turn_id='turn'))
        await self.engine.poll()
        self.assertEqual(self.queue.pending(), [])
        self.assertEqual(self.cards.emitted[-1]['type'], 'terminal')
        self.assertEqual(self.flushes, 1)
        self.assertFalse(self.engine.busy)
        self.assertNotIn('user text', self.engine.journal.read_text())

    async def test_definite_rejection_resets_only_unwritten_input(self):
        self.terminal.ack_mode = 'rejected'
        self.enqueue()
        with self.assertRaises(Exception): await self.engine.deliver('message-1', 'user text')
        self.assertFalse(self.engine.busy)
        self.assertFalse(self.source.blocked)
        self.assertEqual(self.queue.pending()[0]['delivery_state'], 'pending')
        self.terminal.ack_mode = 'written'
        await self.engine.deliver('message-1', 'user text')
        self.assertEqual(len(self.terminal.calls), 2)
        self.assertEqual(self.queue.pending()[0]['delivery_state'], 'delivery_unknown')

    async def test_next_fifo_waits_for_terminal_card_success(self):
        await self.submit(); self.enqueue('message-2', 'second input')
        with self.assertRaises(RuntimeError): await self.engine.deliver('message-2', 'second input')
        self.assertEqual(len(self.terminal.calls), 1)
        self.start_log(); await self.engine.poll()
        self.assertEqual(len(self.terminal.calls), 1)
        self.append(event('task_complete', turn_id='turn')); await self.engine.poll()
        self.assertEqual(len(self.terminal.calls), 2)
        self.assertEqual(self.queue.pending()[0]['message_id'], 'message-2')
        self.assertEqual(self.queue.pending()[0]['delivery_state'], 'delivery_unknown')
        self.start_log(turn='second-turn', public='second progress')
        self.append(event('task_complete', turn_id='second-turn'))
        await self.engine.poll()
        self.assertEqual(self.queue.pending(), [])
        self.assertEqual(len(self.cards.created), 2)
        self.assertNotEqual(self.cards.created[0]['task_id'], self.cards.created[1]['task_id'])

    async def test_restart_with_unknown_never_replays(self):
        await self.submit()
        before = self.queue.path.read_bytes()
        restarted = PublicTaskBridge(self.bridge, source=RolloutSource(self.home, SESSION), cards=FakeCards())
        with self.assertRaises(RuntimeError): await restarted.deliver('message-1', 'user text')
        await restarted.poll()
        self.assertEqual(self.queue.path.read_bytes(), before)
        self.assertEqual(len(self.terminal.calls), 1)

    async def test_unknown_and_wrong_ack_hold_queue_and_block(self):
        for mode in ('unknown', 'wrong-id'):
            with self.subTest(mode=mode):
                self.setUp()
                try:
                    self.terminal.ack_mode = mode
                    self.enqueue()
                    with self.assertRaises(RuntimeError): await self.engine.deliver('message-1', 'user text')
                    self.assertTrue(self.engine.busy)
                    self.assertEqual(self.queue.pending()[0]['delivery_state'], 'delivery_unknown')
                    self.assertTrue(self.source.blocked)
                finally: self.tearDown()

    async def test_descriptor_generation_or_session_drift_no_card_no_ack(self):
        for field, value in [('generation', 4), ('session', 'other')]:
            with self.subTest(field=field):
                self.setUp()
                try:
                    await self.submit(); self.start_log()
                    setattr(self.terminal, field, value)
                    await self.engine.poll()
                    self.assertTrue(self.engine.blocked_reason)
                    self.assertEqual(self.cards.created, [])
                    self.assertEqual(self.queue.pending()[0]['delivery_state'], 'delivery_unknown')
                finally: self.tearDown()

    async def test_card_create_emit_and_terminal_uncertainty_hold_fifo(self):
        for failure in ('create', 'emit', 'terminal'):
            with self.subTest(failure=failure):
                self.setUp()
                try:
                    await self.submit(); self.enqueue('message-2', 'second')
                    self.start_log(); self.append(event('task_complete', turn_id='turn'))
                    self.cards.fail = failure
                    await self.engine.poll()
                    self.assertTrue(self.engine.blocked_reason)
                    self.assertEqual(len(self.queue.pending()), 2)
                    self.assertEqual(len(self.terminal.calls), 1)
                    self.assertEqual(self.flushes, 0)
                finally: self.tearDown()

    async def test_wrong_last_public_identity_discards_entire_batch(self):
        await self.submit(); self.start_log(); self.append(event('task_complete', turn_id='turn'))
        events = self.source.poll(); events[-1]['task_id'] = 'wrong'
        self.source.poll = lambda: events
        await self.engine.poll()
        self.assertEqual(self.cards.created, [])
        self.assertEqual(self.cards.emitted, [])
        self.assertEqual(self.queue.pending()[0]['delivery_state'], 'delivery_unknown')

    async def test_extra_route_field_and_unproven_terminal_rejected_before_card(self):
        for mutation in ('extra-route', 'unproven-terminal'):
            with self.subTest(mutation=mutation):
                self.setUp()
                try:
                    await self.submit(); self.start_log(); events = self.source.poll()
                    if mutation == 'extra-route': events[0]['chat_id'] = 'different-chat'
                    else:
                        terminal = dict(events[0], seq=2, type='terminal', payload={'status': 'success'})
                        events.append(terminal)
                    self.source.poll = lambda: events
                    await self.engine.poll()
                    self.assertEqual(self.cards.created, [])
                    self.assertEqual(len(self.queue.pending()), 1)
                finally: self.tearDown()

    async def test_abort_real_card_and_bound_turn_only_then_explicit_abort(self):
        await self.submit(); self.start_log(); await self.engine.poll()
        task = self.engine.active['task_id']
        for tid, chat, card in [('wrong', 'chat', 'real-card'), (task, 'other', 'real-card'), (task, 'chat', 'forged-card')]:
            self.assertFalse(await self.engine.abort(tid, chat, card))
        self.assertEqual(len(self.terminal.calls), 1)
        self.assertTrue(await self.engine.abort(task, 'chat', 'real-card'))
        self.assertEqual(self.terminal.calls[-1][0], '\x03')
        self.assertEqual(self.terminal.calls[-1][2], {'expected_turn_id': 'turn'})
        self.assertEqual(len(self.queue.pending()), 1)
        self.append(event('turn_aborted', turn_id='turn')); await self.engine.poll()
        self.assertEqual(self.queue.pending(), [])
        self.assertEqual(self.cards.emitted[-1]['payload']['status'], 'cancelled')

    async def test_abort_unknown_and_wrong_ack_cannot_complete_or_retry(self):
        for mode in ('unknown', 'wrong-id'):
            with self.subTest(mode=mode):
                self.setUp()
                try:
                    await self.submit(); self.start_log(); await self.engine.poll()
                    self.terminal.abort_ack_mode = mode
                    task = self.engine.active['task_id']
                    self.assertFalse(await self.engine.abort(task, 'chat', 'real-card'))
                    self.assertTrue(self.engine.blocked_reason)
                    self.assertFalse(await self.engine.abort(task, 'chat', 'real-card'))
                    self.assertEqual(len(self.terminal.calls), 2)
                    self.assertEqual(len(self.queue.pending()), 1)
                finally: self.tearDown()

    async def test_completed_or_ambiguous_turn_does_not_get_control_input(self):
        for extra in ([event('task_complete', turn_id='turn')], [message('user', 'parallel desktop')]):
            self.setUp()
            try:
                await self.submit(); self.start_log(); await self.engine.poll()
                self.append(*extra)
                self.assertFalse(await self.engine.abort(self.engine.active['task_id'], 'chat', 'real-card'))
                self.assertEqual(len(self.terminal.calls), 1)
                self.assertEqual(len(self.queue.pending()), 1)
            finally: self.tearDown()


if __name__ == '__main__':
    unittest.main()
