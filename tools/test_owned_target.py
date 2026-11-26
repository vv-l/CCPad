"""Owned target identity and readiness with fake terminal/SDK only."""
import asyncio
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

import feishu_single_window_demo as demo
from offline_bridge_queue import OfflineBridgeQueue


class OwnedTargetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.descriptor = self.root / 'descriptor.json'
        self.record = dict(base_url='http://127.0.0.1:1', token='fixture-token', pid=42,
                           window_id='window-fixture', pane_id='pane-fixture', generation=3,
                           app_id='cli_ccpad_fixture')
        self.descriptor.write_text(json.dumps(self.record))
        self.args = ['--credentials', str(self.root/'unused'), '--web-descriptor', str(self.descriptor),
                     '--state-dir', str(self.root), '--allowed-chat-id', 'chat-fixture',
                     '--expected-app-id', 'cli_ccpad_fixture', '--target-pane-id', 'pane-fixture',
                     '--window-id', 'window-fixture', '--generation', '3',
                     '--startup-ready', str(self.root/'ready.json'), '--allow-input']
        self.config = demo.parse_args(self.args)

    @patch.object(demo, "RESERVED_APP_FINGERPRINT", hashlib.sha256(b"cli_reserved_fixture").hexdigest())
    def test_explicit_target_requires_complete_identity_and_rejects_reserved_app(self):
        for option, value in [('--generation','0'),('--window-id',''),('--allowed-chat-id',''),
                              ('--expected-app-id','cli_reserved_fixture')]:
            args = list(self.args); args[args.index(option)+1] = value
            with self.assertRaises(ValueError): demo.parse_args(args)
        for option in ('--adopt-first-chat','--auto-bind-unique'):
            with self.assertRaises(ValueError): demo.parse_args(self.args+[option])

    def test_immutable_descriptor_checked_even_if_mtime_unchanged(self):
        link = demo.TerminalLink(self.config)
        link.refresh_descriptor()
        timestamp = self.descriptor.stat().st_mtime_ns
        for field, value in [('window_id','other'),('pane_id','other'),('generation',4),('pid',43),
                             ('app_id','other'),('base_url','http://127.0.0.1:2'),('token','different')]:
            changed = dict(self.record); changed[field] = value
            self.descriptor.write_text(json.dumps(changed))
            os.utime(self.descriptor, ns=(timestamp,timestamp))
            with self.assertRaisesRegex(RuntimeError,'owned_descriptor_identity_mismatch'):
                link.refresh_descriptor()
            self.assertEqual(self.config.web_pid,42)
        self.descriptor.write_text(json.dumps(self.record))
        self.descriptor.unlink()
        with self.assertRaisesRegex(RuntimeError,'owned_descriptor_unavailable'):
            link.refresh_descriptor()

    def test_cannot_rebind_other_pane_or_mismatching_selected_metadata(self):
        link = demo.TerminalLink(self.config)
        with patch.object(demo.websockets,'connect') as connect:
            with self.assertRaisesRegex(RuntimeError,'owned_target_pane_mismatch'):
                asyncio.run(link.bind('other',{'id':'other'}))
            with self.assertRaisesRegex(RuntimeError,'owned_target_metadata_mismatch'):
                asyncio.run(link.bind('pane-fixture',{'id':'other'}))
            connect.assert_not_called()

    def test_dispatch_rejects_changed_generation_without_touching_queue(self):
        bridge = object.__new__(demo.BridgeDemo)
        bridge.config = self.config
        bridge.terminal = demo.TerminalLink(self.config)
        bridge.terminal.session_id = 'pane-fixture'; bridge.terminal._attached.set()
        bridge.offline_queue = OfflineBridgeQueue(self.root/'queue.json')
        bridge.offline_queue.enqueue('fixture-message','fixture-chat','text',text='fixture')
        before = bridge.offline_queue.path.read_bytes()
        changed = dict(self.record,generation=4); self.descriptor.write_text(json.dumps(changed))
        with self.assertRaisesRegex(RuntimeError,'owned_descriptor_identity_mismatch'):
            asyncio.run(bridge.flush_offline_queue())
        self.assertEqual(bridge.offline_queue.path.read_bytes(),before)

    def test_unknown_input_stays_pending_on_owned_binding(self):
        bridge = object.__new__(demo.BridgeDemo)
        bridge.config = self.config; bridge.terminal = demo.TerminalLink(self.config)
        bridge.terminal.session_id = 'pane-fixture'; bridge.terminal._attached.set()
        bridge.terminal.send_input = AsyncMock(); bridge.reply = AsyncMock()
        bridge.offline_queue = OfflineBridgeQueue(self.root/'queue.json')
        bridge.offline_queue.enqueue('fixture-message','fixture-chat','text',text='fixture')
        bridge.offline_queue.begin_delivery('fixture-message')
        self.assertEqual(asyncio.run(bridge.flush_offline_queue()),0)
        bridge.terminal.send_input.assert_not_called()
        self.assertEqual(bridge.offline_queue.pending()[0]['delivery_state'],'delivery_unknown')

    def bootstrap(self, ack=1, fail=False):
        terminal = SimpleNamespace(bind=AsyncMock(side_effect=RuntimeError('fixture') if fail else None),
                                   close=AsyncMock(), _ack_version=ack)
        ready_seen = []
        async def run():
            ready_seen.append(self.config.startup_ready.exists())
        bridge = SimpleNamespace(terminal=terminal, run=AsyncMock(side_effect=run))
        with patch.object(demo,'parse_args',return_value=self.config), patch.object(demo,'read_credentials',return_value=('cli_ccpad_fixture','dummy')), patch.object(demo,'receiver_owner_path',return_value=self.root/'owner.lock'), patch.object(demo,'BridgeDemo',return_value=bridge), contextlib.redirect_stderr(io.StringIO()):
            result = demo.main(['fixture'])
        return result,bridge,ready_seen

    def test_attach_does_not_claim_feishu_ready_before_ws_and_notice(self):
        result,bridge,seen = self.bootstrap()
        self.assertEqual(result,0)
        bridge.terminal.bind.assert_awaited_once_with('pane-fixture')
        self.assertEqual(seen,[False])
        self.assertFalse((self.root/'ready.json.tmp').exists())

    def test_start_failure_has_no_ready_and_closes_terminal(self):
        for ack,fail in [(0,False),(1,True)]:
            result,bridge,seen = self.bootstrap(ack,fail)
            self.assertEqual(result,1); self.assertEqual(seen,[])
            self.assertFalse(self.config.startup_ready.exists())
            bridge.run.assert_not_awaited(); bridge.terminal.close.assert_awaited_once()

    def test_credential_app_mismatch_before_sdk_or_state(self):
        with patch.object(demo,'parse_args',return_value=self.config), patch.object(demo,'read_credentials',return_value=('wrong-app','dummy')), patch.object(demo,'ReceiverOwnerLock') as lock, patch.object(demo,'BridgeDemo') as bridge, contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(demo.main(['fixture']),1)
            lock.assert_not_called(); bridge.assert_not_called()


if __name__ == '__main__': unittest.main()
