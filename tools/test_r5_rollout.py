import json
from pathlib import Path
import tempfile
import unittest
import uuid

from r5_rollout import RolloutBlocked, RolloutSource


SESSION = '11111111-1111-4111-8111-111111111111'
TASK = '22222222222242228222222222222222'
INPUT = 'fixture-input'
WIRE = '[CCPad bridge task: ' + TASK + ']\nuser fixture'


def event(kind, **values):
    return {'type': 'event_msg', 'payload': {'type': kind, **values}}


def user(text=WIRE, **values):
    return {'type': 'response_item', 'payload': {'type': 'message', 'role': 'user',
        'content': [{'type': 'input_text', 'text': text}], **values}}


def assistant(text='public fixture', phase='final_answer', **values):
    return {'type': 'response_item', 'payload': {'type': 'message', 'role': 'assistant',
        'phase': phase, 'content': [{'type': 'output_text', 'text': text}], **values}}


class RolloutTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.home = Path(self.temp.name)
        (self.home / 'sessions').mkdir()
        self.path = self.home / 'sessions' / ('rollout-fixture-' + SESSION + '.jsonl')
        self.path.write_bytes(b'')
        self.append({'type': 'session_meta', 'payload': {'id': SESSION}},
                    event('task_started', turn_id='old'), user('OLD PRIVATE HISTORY'),
                    assistant('OLD PRIVATE ANSWER'), event('task_complete', turn_id='old'))
        self.source = RolloutSource(self.home, SESSION)

    def tearDown(self):
        self.temp.cleanup()

    def append(self, *records):
        with self.path.open('ab') as output:
            for record in records:
                output.write((json.dumps(record) + '\n').encode())

    def arm(self, ack='written'):
        self.source.arm(TASK, INPUT, WIRE)
        if ack:
            self.source.confirm_ack(INPUT, ack)

    def start(self, turn='turn-fixture'):
        self.append(event('task_started', turn_id=turn), user())

    def test_normal_public_and_explicit_terminal_sequence(self):
        self.arm(); self.start()
        self.append(assistant('progress', 'commentary'), assistant('answer'),
                    event('task_complete', turn_id='turn-fixture'))
        result = self.source.poll()
        self.assertEqual([x['type'] for x in result], ['process_message', 'partial_text', 'terminal'])
        self.assertEqual([x['seq'] for x in result], [1, 2, 3])
        self.assertTrue(all(x['task_id'] == TASK and x['input_id'] == INPUT and
                            x['thread_id'] == SESSION and x['turn_id'] == 'turn-fixture' for x in result))
        self.assertTrue(self.source.terminal)
        self.assertFalse(self.source.busy)
        self.assertEqual(result[-1]['payload'], {'status': 'success'})

    def test_unknown_ack_permanent_and_rejected_ack_can_reset(self):
        self.arm('unknown'); self.start(); self.append(assistant())
        self.assertTrue(self.source.blocked); self.assertTrue(self.source.busy)
        self.assertEqual(self.source.poll(), [])
        self.assertFalse(self.source.confirm_ack(INPUT, 'rejected'))
        with self.assertRaises(RolloutBlocked):
            self.source.arm(TASK, INPUT, WIRE)
        fresh = RolloutSource(self.home, SESSION)
        # A new reader cannot mistake the still-running rollout for idle.
        with self.assertRaises(RolloutBlocked):
            fresh.arm(TASK, INPUT, WIRE)
        self.append(event('task_complete', turn_id='turn-fixture'))
        fresh.arm(TASK, INPUT, WIRE)
        self.assertTrue(fresh.confirm_ack(INPUT, 'rejected'))
        self.assertFalse(fresh.busy)
        fresh.arm(TASK, INPUT, WIRE)

    def test_wrong_ack_id_and_busy_rearm_rejected(self):
        self.arm(None)
        with self.assertRaises(RolloutBlocked):
            self.source.arm(TASK, INPUT, WIRE)
        self.assertFalse(self.source.confirm_ack('other', 'written'))
        self.assertTrue(self.source.blocked)

    def test_private_reasoning_tools_unknown_phase_never_emitted(self):
        self.arm(); self.start()
        self.append({'type': 'response_item', 'payload': {'type': 'reasoning',
            'content': 'PRIVATE_REASONING', 'encrypted_content': 'PRIVATE_ENCRYPTED'}},
            {'type': 'response_item', 'payload': {'type': 'function_call', 'arguments': 'PRIVATE_TOOL'}},
            assistant('PRIVATE_ANALYSIS', 'analysis'), assistant('PRIVATE_MISSING', None),
            event('agent_message', message='UNVERIFIED_PUBLIC_EVENT'), assistant('approved'))
        result = self.source.poll()
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]['payload']['text'], 'approved')
        self.assertNotIn('PRIVATE', json.dumps(result))
        self.assertNotIn('UNVERIFIED', json.dumps(result))

    def test_ambiguous_desktop_user_discards_entire_batch(self):
        self.arm(); self.start()
        self.append(assistant('must not escape'), user('desktop simultaneous'), assistant('other'))
        self.assertEqual(self.source.poll(), [])
        self.assertTrue(self.source.blocked)

    def test_wrong_missing_turn_or_user_never_public(self):
        baseline = self.path.read_bytes()
        cases = [([event('task_started'), user(), assistant()], 'ambiguous'),
                 ([event('task_started', turn_id='turn'), assistant()], 'before'),
                 ([event('task_started', turn_id='turn'), user('different'), assistant()], 'nonmatching'),
                 ([event('task_started', turn_id='turn'), user(), assistant(turn_id='wrong')], 'turn_identity'),
                 ([event('task_started', turn_id='turn'), user(), assistant(thread_id='wrong')], 'thread_identity'),
                 ([event('task_started', turn_id='turn'), user(), event('task_complete', turn_id='wrong')], 'turn_identity')]
        for records, reason in cases:
            source = RolloutSource(self.home, SESSION); source.arm(TASK, INPUT, WIRE); source.confirm_ack(INPUT, 'written')
            self.append(*records)
            self.assertEqual(source.poll(), [])
            self.assertTrue(source.blocked)
            self.path.write_bytes(baseline)

    def test_idle_requires_terminal_matching_latest_started_turn(self):
        baseline = self.path.read_bytes()
        for started, ended in [(None, 'unmatched'), ('active', 'wrong'), ('active', None)]:
            self.path.write_bytes(baseline)
            if started:
                self.append(event('task_started', turn_id=started))
            self.append(event('task_complete', turn_id=ended))
            with self.assertRaises(RolloutBlocked):
                RolloutSource(self.home, SESSION).arm(TASK, INPUT, WIRE)
        self.path.write_bytes(baseline)
        self.append(event('task_started', turn_id='valid'), event('task_complete', turn_id='valid'))
        RolloutSource(self.home, SESSION).arm(TASK, INPUT, WIRE)

    def test_old_history_and_read_past_eof_do_not_replay(self):
        self.arm()
        self.assertEqual(self.source.poll(), [])
        self.start(); self.append(assistant('new-only'))
        result = self.source.poll()
        self.assertEqual(result[0]['payload']['text'], 'new-only')
        self.assertEqual(self.source.poll(), [])
        self.assertNotIn('OLD', json.dumps(result))

    def test_partial_jsonl_holds_whole_batch_until_complete(self):
        self.arm(); self.start(); self.append(assistant('held'))
        partial = json.dumps(event('task_complete', turn_id='turn-fixture')).encode()
        with self.path.open('ab') as output: output.write(partial[:10])
        self.assertEqual(self.source.poll(), [])
        self.assertFalse(self.source.blocked)
        with self.path.open('ab') as output: output.write(partial[10:] + b'\n')
        self.assertEqual([x['type'] for x in self.source.poll()], ['partial_text', 'terminal'])

    def test_rotation_truncation_rewrite_and_duplicate_file_block(self):
        for mutation in ('truncate', 'replace', 'rewrite', 'duplicate'):
            source = RolloutSource(self.home, SESSION); source.arm(TASK, INPUT, WIRE); source.confirm_ack(INPUT, 'written')
            original = self.path.read_bytes()
            if mutation == 'truncate': self.path.write_bytes(b'{}\n')
            elif mutation == 'replace':
                old = self.path.with_suffix('.old'); self.path.rename(old); self.path.write_bytes(original); old.unlink()
            elif mutation == 'rewrite': self.path.write_bytes(original.replace(b'OLD PRIVATE HISTORY', b'BAD PRIVATE HISTORY'))
            else:
                other = self.path.with_name('rollout-other-' + SESSION + '.jsonl'); other.write_bytes(original)
            self.assertEqual(source.poll(), [])
            self.assertTrue(source.blocked)
            if mutation == 'duplicate': other.unlink()
            self.path.write_bytes(original)

    def test_session_metadata_changed_and_bad_history_identity_block(self):
        self.arm(); self.start(); self.append({'type': 'session_meta', 'payload': {'id': str(uuid.uuid4())}})
        self.assertEqual(self.source.poll(), []); self.assertTrue(self.source.blocked)
        self.path.write_text(json.dumps({'type': 'session_meta', 'payload': {'id': 'other'}}) + '\n')
        with self.assertRaises(RolloutBlocked):
            RolloutSource(self.home, SESSION).arm(TASK, INPUT, WIRE)

    def test_explicit_abort_and_next_arm_dont_import_new_desktop_turn(self):
        self.arm(); self.start(); self.append(event('turn_aborted', turn_id='turn-fixture'),
            event('task_started', turn_id='desktop'), user('desktop text'), assistant('desktop output'))
        result = self.source.poll()
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]['payload']['status'], 'cancelled')
        self.assertEqual(self.source.poll(), [])
        with self.assertRaises(RolloutBlocked): self.source.arm(TASK, INPUT, WIRE)

    def test_logical_wire_not_terminal_cr_and_read_before_ack(self):
        with self.assertRaises(ValueError): self.source.arm(TASK, INPUT, WIRE + '\r')
        self.arm(None); self.start(); self.append(assistant())
        self.assertEqual(self.source.poll(), [])
        self.assertTrue(self.source.confirm_ack(INPUT, 'written'))
        self.assertEqual(len(self.source.poll()), 1)


if __name__ == '__main__':
    unittest.main()
