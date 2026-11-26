"""Exact local Codex rollout correlation; no terminal output or model invocation."""
import hashlib
import json
from pathlib import Path
import uuid


class RolloutBlocked(ValueError):
    pass


class RolloutSource:
    def __init__(self, home, session_id):
        self.home = Path(home)
        if not self.home.is_absolute():
            raise ValueError('absolute_codex_home_required')
        self.session_id = str(uuid.UUID(session_id))
        self._task = None
        self._wire = ''
        self._path = None
        self._file_id = None
        self._offset = 0
        self._prefix = None
        self.blocked_reason = None

    @property
    def blocked(self):
        return self.blocked_reason is not None

    @property
    def terminal(self):
        return bool(self._task and self._task['state'] in ('complete', 'aborted'))

    @property
    def busy(self):
        return self._task is not None and not self.terminal

    @property
    def active(self):
        return dict(self._task) if self._task else None

    @property
    def current(self):
        return self.active

    def _block(self, reason):
        self.blocked_reason = reason
        if self._task:
            self._task['state'] = 'blocked'
        return []

    def _find(self):
        paths = list((self.home / 'sessions').glob('**/rollout-*-' + self.session_id + '.jsonl'))
        if len(paths) != 1 or paths[0].is_symlink():
            raise RolloutBlocked('exact_session_rollout_unavailable_or_ambiguous')
        return paths[0]

    def _snapshot(self, path):
        before = path.stat()
        data = path.read_bytes()
        after = path.stat()
        if (before.st_ino, before.st_size, before.st_mtime_ns) != (after.st_ino, after.st_size, after.st_mtime_ns):
            return None
        return data, (after.st_dev, after.st_ino)

    def _records(self, data):
        for line in data.splitlines():
            value = json.loads(line)
            if not isinstance(value, dict):
                raise RolloutBlocked('invalid_rollout_record')
            yield value

    def _history_idle(self, data):
        if not data or not data.endswith(b'\n'):
            raise RolloutBlocked('idle_not_proven_partial_history')
        identity, idle, active_turn = 0, None, None
        for record in self._records(data):
            payload = record.get('payload')
            if not isinstance(payload, dict):
                continue
            if record.get('type') == 'session_meta':
                if payload.get('id') != self.session_id:
                    raise RolloutBlocked('session_identity_mismatch')
                identity += 1
            elif record.get('type') == 'event_msg':
                kind = payload.get('type')
                if kind == 'task_started':
                    turn = payload.get('turn_id')
                    active_turn = turn if isinstance(turn, str) and turn else None
                    idle = False
                elif kind == 'user_message':
                    idle = False
                elif kind in ('task_complete', 'turn_aborted'):
                    turn = payload.get('turn_id')
                    idle = active_turn is not None and turn == active_turn
                    if idle:
                        active_turn = None
            elif record.get('type') == 'response_item' and payload.get('type') == 'message' and payload.get('role') == 'user':
                idle = False
        if identity != 1 or idle is not True:
            raise RolloutBlocked('exact_session_idle_not_proven')

    def arm(self, task_id, input_id, wire_text):
        if self.blocked or self.busy:
            raise RolloutBlocked('source_busy_or_blocked')
        if not all(isinstance(value, str) and value for value in (task_id, input_id, wire_text)):
            raise ValueError('explicit_task_input_wire_required')
        if '\r' in wire_text or not wire_text.startswith('[CCPad bridge task: ' + task_id + ']\n'):
            raise ValueError('logical_wire_marker_required_without_terminal_cr')
        path = self._find()
        snapshot = self._snapshot(path)
        if snapshot is None:
            raise RolloutBlocked('rollout_changing_during_arm')
        data, file_id = snapshot
        self._history_idle(data)
        self._path, self._file_id = path, file_id
        self._offset, self._prefix = len(data), hashlib.sha256(data).digest()
        self._wire = wire_text
        self._task = {'task_id': task_id, 'input_id': input_id, 'thread_id': self.session_id,
                      'turn_id': None, 'state': 'awaiting_ack', 'seq': 0}
        return self.active

    def confirm_ack(self, input_id, status):
        if self.blocked:
            return False
        if not self._task or input_id != self._task['input_id']:
            self._block('ack_input_identity_mismatch')
            return False
        if self._task['state'] != 'awaiting_ack':
            if status == 'written' and self._task['state'] in ('awaiting_start', 'awaiting_user', 'bound'):
                return True
            self._block('conflicting_or_late_ack')
            return False
        if status == 'rejected':
            self._task = None
            self._wire = ''
            return True
        if status != 'written':
            self._block('ack_unknown_no_replay')
            return False
        self._task['state'] = 'awaiting_start'
        return True

    def _text(self, payload, allowed):
        content = payload.get('content')
        if not isinstance(content, list) or not content:
            return None
        if any(not isinstance(part, dict) or part.get('type') not in allowed or
               not isinstance(part.get('text'), str) for part in content):
            return None
        return ''.join(part['text'] for part in content)

    def _consume(self, record, task, events):
        payload = record.get('payload')
        if not isinstance(payload, dict):
            return
        if record.get('type') == 'session_meta':
            raise RolloutBlocked('session_metadata_changed_after_arm')
        if task['state'] in ('complete', 'aborted'):
            return  # Subsequent desktop turns belong to no bridge task.
        for value in (record.get('thread_id'), payload.get('thread_id')):
            if value is not None and value != self.session_id:
                raise RolloutBlocked('thread_identity_mismatch')
        if (payload.get('turn_id') is not None and payload.get('type') != 'task_started' and
                payload['turn_id'] != task['turn_id']):
            raise RolloutBlocked('turn_identity_mismatch')
        if record.get('type') == 'turn_context':
            supplied = payload.get('turn_id')
            if supplied and supplied != task['turn_id']:
                raise RolloutBlocked('turn_identity_mismatch')
            return
        if record.get('type') == 'event_msg':
            kind = payload.get('type')
            if kind == 'task_started':
                turn = payload.get('turn_id')
                if task['state'] != 'awaiting_start' or not isinstance(turn, str) or not turn:
                    raise RolloutBlocked('ambiguous_or_missing_turn_start')
                task.update(turn_id=turn, state='awaiting_user')
            elif kind == 'user_message':
                raise RolloutBlocked('unsupported_or_extra_user_event')
            elif kind in ('task_complete', 'turn_aborted'):
                if task['state'] != 'bound' or payload.get('turn_id') != task['turn_id']:
                    raise RolloutBlocked('terminal_identity_or_correlation_missing')
                task['state'] = 'complete' if kind == 'task_complete' else 'aborted'
                self._emit(task, events, 'terminal', {'status': 'success' if kind == 'task_complete' else 'cancelled'})
            # event_msg agent_message has no verified public phase contract here.
            return
        if record.get('type') != 'response_item' or payload.get('type') != 'message':
            return  # Never inspect reasoning, encrypted fields or tool records.
        role = payload.get('role')
        if role == 'user':
            text = self._text(payload, {'input_text', 'text'})
            if task['state'] != 'awaiting_user' or text != self._wire:
                raise RolloutBlocked('parallel_or_nonmatching_user_message')
            task['state'] = 'bound'
        elif role == 'assistant':
            if task['state'] != 'bound':
                raise RolloutBlocked('assistant_before_unique_user_correlation')
            phase = payload.get('phase')
            if phase not in ('commentary', 'final_answer'):
                return
            text = self._text(payload, {'output_text'})
            if text is None:
                return
            self._emit(task, events, 'process_message' if phase == 'commentary' else 'partial_text',
                       {'text': text, 'phase': phase})

    def _emit(self, task, events, kind, payload):
        task['seq'] += 1
        events.append({key: task[key] for key in ('task_id', 'input_id', 'thread_id', 'turn_id', 'seq')} |
                      {'type': kind, 'payload': payload})

    def poll(self):
        if not self._task or self.blocked or self.terminal or self._task['state'] == 'awaiting_ack':
            return []
        try:
            if self._find() != self._path:
                return self._block('rollout_path_rotated')
            snapshot = self._snapshot(self._path)
            if snapshot is None:
                return []
            data, file_id = snapshot
            if file_id != self._file_id or len(data) < self._offset or hashlib.sha256(data[:self._offset]).digest() != self._prefix:
                return self._block('rollout_rotated_truncated_or_rewritten')
            pending = data[self._offset:]
            if not pending or not pending.endswith(b'\n'):
                return []
            task, events = dict(self._task), []
            for record in self._records(pending):
                self._consume(record, task, events)
            self._task = task
            self._offset, self._prefix = len(data), hashlib.sha256(data).digest()
            return events
        except (OSError, ValueError, TypeError) as error:
            reason = str(error) if isinstance(error, RolloutBlocked) else 'rollout_read_or_schema_invalid'
            return self._block(reason)
