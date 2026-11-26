"""Local owner fixtures; no credentials, SDK listener or model calls."""
import asyncio
import contextlib
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import AsyncMock, patch

import feishu_single_window_demo as demo
from offline_bridge_queue import OfflineBridgeQueue


class ReceiverOwnerTests(unittest.TestCase):
    def start_holder(self, path):
        code = ("import sys; from pathlib import Path; "
                "from feishu_single_window_demo import ReceiverOwnerLock; "
                "lock=ReceiverOwnerLock(Path(sys.argv[1])); "
                "print('held',flush=True); sys.stdin.readline(); lock.close()")
        child = subprocess.Popen([sys.executable, '-B', '-c', code, str(path)],
                                 cwd=Path(__file__).parent, stdin=subprocess.PIPE,
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                 text=True)
        self.addCleanup(self.close_child, child)
        self.assertEqual(child.stdout.readline().strip(), 'held')
        return child

    @staticmethod
    def close_child(child):
        if child.poll() is None:
            child.terminate()
        child.wait(timeout=10)
        for stream in (child.stdin, child.stdout, child.stderr):
            stream.close()

    def test_two_processes_reject_second_and_release_on_normal_exit(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'app.lock'
            child = self.start_holder(path)
            with self.assertRaises(demo.ReceiverOwnerBusy):
                demo.ReceiverOwnerLock(path)
            child.stdin.write('\n')
            child.stdin.flush()
            self.assertEqual(child.wait(timeout=10), 0)
            lock = demo.ReceiverOwnerLock(path)
            lock.close()
            lock.close()

    def test_os_releases_owner_after_abnormal_process_exit(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'app.lock'
            child = self.start_holder(path)
            child.terminate()
            child.wait(timeout=10)
            lock = demo.ReceiverOwnerLock(path)
            lock.close()

    def test_stale_file_cannot_bypass_live_owner(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'app.lock'
            path.write_bytes(b'0 stale PID and generation')
            child = self.start_holder(path)
            with self.assertRaises(demo.ReceiverOwnerBusy):
                demo.ReceiverOwnerLock(path)
            child.terminate()
            child.wait(timeout=10)
            lock = demo.ReceiverOwnerLock(path)
            lock.close()

    def test_app_scope_is_independent_of_state_and_other_app(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(demo.Path, 'home', return_value=Path(folder)):
            path = demo.receiver_owner_path('cli_fixture_a')
            self.assertEqual(path, demo.receiver_owner_path('cli_fixture_a'))
            self.assertNotEqual(path, demo.receiver_owner_path('cli_fixture_b'))
            first = demo.ReceiverOwnerLock(path)
            other = demo.ReceiverOwnerLock(demo.receiver_owner_path('cli_fixture_b'))
            try:
                with self.assertRaises(demo.ReceiverOwnerBusy):
                    demo.ReceiverOwnerLock(demo.receiver_owner_path('cli_fixture_a'))
            finally:
                other.close()
                first.close()

    def test_main_busy_rejects_before_sdk_or_state_constructor(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'app.lock'
            child = self.start_holder(path)
            config = demo.BridgeConfig(Path('unused'), 'http://127.0.0.1:1', '', Path(folder)/'state', 0, True)
            with patch.object(demo, 'parse_args', return_value=config), patch.object(demo, 'read_credentials', return_value=('cli_fixture', 'dummy')), patch.object(demo, 'receiver_owner_path', return_value=path), patch.object(demo, 'BridgeDemo') as bridge, contextlib.redirect_stderr(io.StringIO()) as error:
                self.assertEqual(demo.main(['fixture']), 1)
                bridge.assert_not_called()
                self.assertIn('ReceiverOwnerBusy', error.getvalue())
                self.assertNotIn('dummy', error.getvalue())
                self.assertFalse(config.state_dir.exists())
            child.terminate()
            child.wait(timeout=10)

    def test_main_exception_releases_lock_and_uses_locked_credentials(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'app.lock'
            config = demo.BridgeConfig(Path('unused'), 'http://127.0.0.1:1', '', Path(folder)/'state', 0, True)
            with patch.object(demo, 'parse_args', return_value=config), patch.object(demo, 'read_credentials', return_value=('cli_fixture', 'dummy')) as read, patch.object(demo, 'receiver_owner_path', return_value=path), patch.object(demo, 'BridgeDemo') as bridge, contextlib.redirect_stderr(io.StringIO()):
                bridge.return_value.run = AsyncMock(side_effect=ValueError('fixture failure'))
                self.assertEqual(demo.main(['fixture']), 1)
                read.assert_called_once()
                bridge.assert_called_once_with(config, credentials=('cli_fixture', 'dummy'))
            lock = demo.ReceiverOwnerLock(path)
            lock.close()

    def test_unknown_queue_survives_owner_restart_without_send(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            queue = OfflineBridgeQueue(folder/'queue.json')
            queue.enqueue('fixture-message', 'fixture-chat', 'text', text='fixture')
            queue.begin_delivery('fixture-message')
            before = queue.pending()[0]
            child = self.start_holder(folder/'app.lock')
            child.terminate()
            child.wait(timeout=10)
            owner = demo.ReceiverOwnerLock(folder/'app.lock')
            try:
                bridge = object.__new__(demo.BridgeDemo)
                bridge.offline_queue = OfflineBridgeQueue(queue.path)
                bridge._can_deliver_input = lambda: True
                bridge.reply = AsyncMock()
                bridge.terminal = type('FakeTerminal', (), {'send_input': AsyncMock(), 'session_id': 'fixture-pane'})()
                self.assertEqual(asyncio.run(bridge.flush_offline_queue()), 0)
                bridge.terminal.send_input.assert_not_called()
                after = queue.pending()
                self.assertEqual(len(after), 1)
                for key in ('input_id', 'delivery_state', 'message_id_sha256', 'text'):
                    self.assertEqual(after[0][key], before[key])
                self.assertEqual(after[0]['delivery_state'], 'delivery_unknown')
            finally:
                owner.close()


if __name__ == '__main__':
    unittest.main()
