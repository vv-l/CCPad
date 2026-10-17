"""Offline staging regressions against the actual embedded HTML and bundled xterm.

python tests/staged_queue.py [--source <TerminalPane.xaml.cs>] [--report <json>]
Uses a fake WebView host and fictional terminal output. Never opens a CLI/PTY,
uses SSH, or submits a real prompt. --source supports a pre-fix comparison.
"""
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
import argparse
import base64
import json
import re
import threading
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', type=Path, default=ROOT / 'CCPad/TerminalPane.xaml.cs')
parser.add_argument('--report', type=Path)
args = parser.parse_args()
code = args.source.read_text(encoding='utf-8')
html = re.search(r'private const string TerminalHtml = """\s*\n(.*?)\n\s*""";', code, re.S).group(1)
html = html.replace('https://xterm.local/', '/xterm/').replace('__INITIAL_DARK__', 'true').replace('__INITIAL_LASTCMD__', 'true')
html = html.replace('<head>', '''<head><script>
window.__sent=[];window.__listeners=[];
window.chrome=window.chrome||{};
window.chrome.webview={
 addEventListener(type,fn){if(type==='message')__listeners.push(fn)},
 postMessage(raw){__sent.push(JSON.parse(raw))}};
window.__host=msg=>__listeners.forEach(fn=>fn({data:JSON.stringify(msg)}));
</script>''', 1)

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *unused): pass
    def do_GET(self):
        if self.path == '/':
            data, kind = html.encode(), 'text/html'
        elif self.path.startswith('/xterm/') and Path(self.path).name in {
            'xterm.js', 'xterm.css', 'xterm-addon-fit.js', 'ccpad-input-compat.js'}:
            path = ROOT / 'CCPad/Assets/xterm' / Path(self.path).name
            data, kind = path.read_bytes(), 'text/css' if path.suffix == '.css' else 'text/javascript'
        else:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header('Content-Type', kind + '; charset=utf-8')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

server = HTTPServer(('127.0.0.1', 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
results, errors = [], []
input_cases = []

try:
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True, args=['--disable-gpu'])
        page = browser.new_page(viewport={'width': 1100, 'height': 900})
        page.on('pageerror', lambda error: errors.append(str(error)))

        def host(msg): page.evaluate('msg=>__host(msg)', msg)
        def status(value='waiting', pending=False):
            host({'type': 'paneStatus', 'status': value, 'automationPending': pending})
        def sent(): return page.evaluate("__sent.filter(m=>m.type==='stagedInput')")
        def wait(): page.wait_for_timeout(560)
        def expect_count(count):
            actual = len(sent())
            assert actual == count, f'Expected {count} individual sends, got {actual}'
        def queued(): return page.locator('#stage-list .txt').all_text_contents()
        def output(text):
            host({'type': 'output', 'data': base64.b64encode(text.encode()).decode()})
            page.wait_for_timeout(35)
        def screen(draft=None, busy='', bracketed=True, dim=True):
            rows = page.evaluate('term.rows')
            prompt = rows - 3
            text = '\x1b[0m\x1b[2J\x1b[H' + ('\x1b[?2004h' if bracketed else '\x1b[?2004l')
            if busy:
                text += f'\x1b[{prompt - 4};1H{busy}'
            body = '\x1b[2mAsk Codex to do anything\x1b[0m' if draft is None and dim else (draft or 'Some draft')
            text += f'\x1b[{prompt};1H\x1b[1m›\x1b[0m {body}'
            text += f'\x1b[{prompt + 2};1HGPT fixture\x1b[{prompt + 3};1H? for shortcuts'
            # Put the cursor at Home even for a draft: cursor position alone is
            # not proof that the composer is empty.
            text += f'\x1b[{prompt};3H'
            output(text)
        def enqueue(items):
            for item in items:
                page.locator('#stage-input').fill(item)
                page.locator('#stage-input').press('Enter')
        def reset(items=('case one', 'case two', 'case three'), remote=False):
            page.goto(f'http://127.0.0.1:{server.server_port}/')
            page.wait_for_selector('.xterm-helper-textarea', state='attached')
            status('working')
            host({'type': 'setRemotePaste', 'on': remote})
            host({'type': 'setStaging', 'on': True})
            enqueue(items)
            screen()
        def ack(index=0, request_id=None):
            request_id = sent()[index].get('requestId', index + 1) if request_id is None else request_id
            host({'type': 'stagedInputSubmitted', 'requestId': request_id})
        def check(name, action):
            try:
                action()
                result = {'case': name, 'passed': True}
            except Exception as error:
                result = {'case': name, 'passed': False, 'error': str(error)}
            results.append(result)
            print(('PASS ' if result['passed'] else 'FAIL ') + name)

        def stale_waiting():
            reset(); status(); wait(); expect_count(1)
            for _ in range(3): status(); wait()
            expect_count(1)
            assert len(queued()) == 2
        check('stale idle polls cannot release an unacknowledged item', stale_waiting)

        def local_order():
            items = ['非法名称：报告?.txt', '/tmp/fictional.png 重复目标\n第二行仍属同一条', '目标占用：测试🙂' * 500]
            reset(items); status(); wait()
            for i in range(len(items)):
                expect_count(i + 1)
                assert sent()[i]['data'] == items[i]
                assert sent()[i].get('bracketedPaste') is True
                ack(i); status('working'); wait(); expect_count(i + 1)
                status(); wait()
            assert [m['data'] for m in sent()] == items
            assert len({m['requestId'] for m in sent()}) == 3
            assert queued() == []
        check('long Unicode and multiline entries remain three ordered messages', local_order)

        def remote_unsubmitted():
            reset(remote=True); status(); wait(); expect_count(1)
            screen(draft='case one'); ack(); status(); wait(); expect_count(1)
            for _ in range(3): status(); wait()
            expect_count(1)
            screen(busy='• Compacting context (32s • esc to interrupt)')
            status(); wait(); expect_count(1)
            screen(); status(); wait(); expect_count(2)
        check('unsubmitted remote draft and compaction never concatenate the next item', remote_unsubmitted)

        def initial_draft():
            reset(remote=True); screen(draft='已有手动输入'); status(); wait(); expect_count(0)
            screen(); wait(); expect_count(1)
            assert not page.evaluate("__sent.some(m=>m.type==='input')"), 'Copy-mode key must belong to host send gate'
        check('remote manual draft is preserved, with copy-mode exit owned by host', initial_draft)

        def missing_repaint():
            reset(remote=True); status(); wait(); expect_count(1)
            ack(); status(); wait(); expect_count(1)
            # An old pre-send empty screen is not a receipt.
            status(); wait(); expect_count(1)
            screen(); wait(); expect_count(2)
        check('remote transport ack still requires a fresh empty composer', missing_repaint)

        def fast_completion():
            reset(remote=True); status(); wait(); expect_count(1)
            screen(draft='case one')
            screen()
            ack(); status(); wait(); expect_count(2)
        check('fast remote completion before transport acknowledgement does not stall', fast_completion)

        def compaction():
            reset(remote=True)
            screen(busy='• Compacting context (32s • esc to interrupt)')
            status(); wait(); expect_count(0)
            status(); wait(); expect_count(0)
            screen(); wait(); expect_count(1)
        check('first queued message also waits during compaction', compaction)

        def question():
            reset(remote=True)
            screen(busy='Queued follow-up inputs\r\n? 1 question\r\n  shift+tab to answer')
            status(); wait(); expect_count(0)
            screen(); wait(); expect_count(1)
        check('queued follow-up question blocks automatic task input', question)

        def wrong_receipts():
            reset(); status(); wait(); expect_count(1)
            ack(request_id=999)
            host({'type': 'stagedInputFailed', 'requestId': 999, 'data': 'wrong', 'phase': 'text_write'})
            status(); wait(); expect_count(1)
            assert 'wrong' not in queued()
            ack(); status(); wait(); expect_count(2)
            ack(request_id=1); status(); wait(); expect_count(2)
        check('delayed or mismatched receipts cannot release another item', wrong_receipts)

        def failure():
            reset(); status(); wait(); expect_count(1)
            msg = {'type': 'stagedInputFailed', 'requestId': sent()[0].get('requestId', 1),
                   'data': 'case one', 'phase': 'enter_write'}
            host(msg); host(msg); status(); wait(); expect_count(1)
            assert queued() == ['case one', 'case two', 'case three']
            host({'type': 'setStaging', 'on': False})
            host({'type': 'setStaging', 'on': True}); wait(); expect_count(2)
            assert sent()[1]['data'] == 'case one'
        check('failed item returns once in original order and pauses until explicit resume', failure)

        def automation():
            reset(); status(pending=True); wait(); expect_count(0)
            status(); wait(); expect_count(1)
            ack(); status(pending=True); wait(); expect_count(1)
            status(); wait(); expect_count(2)
        check('auto-enter and auto-reply continue to hold the input gate', automation)

        def disabled():
            reset(); status()
            host({'type': 'setStaging', 'on': False}); wait(); expect_count(0)
            host({'type': 'setStaging', 'on': True}); wait(); expect_count(1)
        check('turning staging off cancels a scheduled flush', disabled)

        def unknown_layout():
            reset(remote=True); screen(dim=False); status(); wait(); expect_count(0)
            screen(bracketed=False); status(); wait(); expect_count(0)
            screen(); wait(); expect_count(1)
        check('unknown composer or missing paste mode waits without injecting text', unknown_layout)

        def bulk():
            reset(items=())
            page.locator('#stage-bulk-toggle').click()
            page.locator('#stage-bulk-input').fill('first\n\nsecond\nthird')
            page.locator('#stage-bulk-import').click()
            status(); wait()
            for i in range(3):
                expect_count(i + 1)
                ack(i); status(); wait()
            assert [m['data'] for m in sent()] == ['first', 'second', 'third']
        check('batch import drains one entry per acknowledged turn', bulk)

        def output_race():
            reset(remote=True); status()
            # Pending flush timer must re-check the latest rendered composer.
            screen(draft='new manual text'); wait(); expect_count(0)
            screen(); wait(); expect_count(1)
        check('composer changes during settle delay cancel the flush', output_race)

        def terminal_reports():
            reset(items=('one message',))
            status(); wait(); expect_count(1)
            output('\x1b[?1004h')
            page.evaluate('term.focus()')
            page.locator('#stage-input').focus()
            output('\x1b[6n\x1b[c\x1b[>c')
            reports = page.evaluate("__sent.filter(m=>m.type==='input')")
            assert any(m['data'] == '\x1b[I' for m in reports)
            assert any(m['data'] == '\x1b[O' for m in reports)
            assert any(re.fullmatch(r'\x1b\[\d+;\d+R', m['data']) for m in reports)
            for m in reports:
                assert m.get('source') != 'keyboard', 'A generated report was tagged as a physical key'
                input_cases.append({**m, 'interrupt': False})
            page.locator('.xterm-helper-textarea').press('Shift+F3')
            key = page.evaluate("__sent.filter(m=>m.type==='input').at(-1)")
            assert key['data'] == '\x1b[1;2R' and key.get('source') == 'keyboard'
            input_cases.append({**key, 'interrupt': True})
        check('real xterm focus/query reports remain distinct from a physical CPR-shaped key', terminal_reports)
        def restore_queue_and_panel():
            reset(items=('执行完成并且登记好以后给我发飞书通知', '第二条\n保留换行'))
            host({'type': 'restoreStaging', 'generation': 7, 'state': {
                'Enabled': True, 'Queue': ['执行完成并且登记好以后给我发飞书通知', '第二条\n保留换行']}})
            page.locator('#stage-input').fill('尚未寄存的草稿')
            page.locator('#stage-bulk-toggle').click()
            page.locator('#stage-bulk-input').fill('批量草稿一\n批量草稿二')
            saved = page.evaluate("__sent.filter(m=>m.type==='stagingState' && m.generation===7).at(-1)?.state")
            assert saved is not None, 'staging queue and open panel were never saved to the host'
            page.reload(); page.wait_for_function("__sent.some(m=>m.type==='ready')")
            status('working')
            host({'type': 'restoreStaging', 'generation': 8, 'state': saved})
            assert page.locator('#stage').evaluate("e=>e.classList.contains('open')")
            assert queued() == ['执行完成并且登记好以后给我发飞书通知', '第二条 ⏎ 保留换行']
            assert page.locator('#stage-input').input_value() == '尚未寄存的草稿'
            assert page.locator('#stage-bulk-input').input_value() == '批量草稿一\n批量草稿二'
            assert page.locator('#stage-bulk-row').evaluate("e=>e.classList.contains('open')")
            wait(); expect_count(0)
        check('close and reopen preserves the reported queue, drafts and expanded panel', restore_queue_and_panel)
        def persisted():
            return page.evaluate("__sent.filter(m=>m.type==='stagingState').at(-1).state")
        def reopen(saved):
            page.reload(); page.wait_for_function("__sent.some(m=>m.type==='ready')")
            host({'type': 'restoreStaging', 'generation': 2, 'state': saved})
        def restored(items=('first', 'second'), enabled=True):
            reset(items=())
            host({'type': 'restoreStaging', 'generation': 1,
                  'state': {'Enabled': enabled, 'Queue': list(items)}})
            status('working')

        def uncertain_delivery():
            restored(); status(); wait(); expect_count(1)
            saved = persisted()
            assert saved['PendingCommand'] == 'first' and saved['Queue'] == ['second']
            reopen(saved); status(); wait(); expect_count(0)
            assert queued() == ['first', 'second']
            assert '确认发送状态' in page.locator('#stage-status').inner_text()
            host({'type': 'setStaging', 'on': False}); host({'type': 'setStaging', 'on': True})
            status(); wait(); expect_count(1)
            assert sent()[0]['data'] == 'first'
        check('unacknowledged send is retained but never automatically repeated after reopen', uncertain_delivery)

        def confirmed_delivery():
            restored(); status(); wait(); expect_count(1); ack()
            saved = persisted()
            assert saved['PendingCommand'] is None and saved['Queue'] == ['second']
            reopen(saved); wait(); expect_count(0)
            status(); wait(); expect_count(1)
            assert sent()[0]['data'] == 'second'
        check('acknowledged content is not requeued and remaining content resumes on fresh idle', confirmed_delivery)

        def stale_renderer_receipt():
            restored(items=('first', 'second', 'third')); status(); wait(); expect_count(1)
            old_id = sent()[0]['requestId']; ack()
            reopen(persisted()); status(); wait(); expect_count(1)
            assert sent()[0]['requestId'] != old_id
            ack(request_id=old_id); status(); wait(); expect_count(1)
            ack(); status(); wait(); expect_count(2)
        check('a receipt from the closed page cannot release a send on the restored page', stale_renderer_receipt)

        def collapsed_panel():
            restored(enabled=False)
            saved = persisted(); reopen(saved); status(); wait(); expect_count(0)
            assert not page.locator('#stage').evaluate("e=>e.classList.contains('open')")
            assert queued() == ['first', 'second']
        check('closed staging panel stays closed while preserving its queue', collapsed_panel)

        def edited_draft():
            restored(); page.locator('#stage-list .edit').first.click()
            page.locator('#stage-list .edit-box').fill('编辑中的内容\n尚未确认')
            saved = persisted(); reopen(saved); status(); wait(); expect_count(0)
            assert page.locator('#stage-list .edit-box').input_value() == '编辑中的内容\n尚未确认'
            page.locator('#stage-list .edit-box').press('Enter'); wait(); expect_count(1)
            assert sent()[0]['data'] == '编辑中的内容\n尚未确认'
        check('inline editing survives reopen without prematurely sending its original text', edited_draft)

        def final_capture():
            restored(); status()
            captured = page.evaluate("() => {document.getElementById('stage-input').value='最后一刻的草稿'; return ccpadCaptureStaging(true)}")
            assert captured['state']['Draft'] == '最后一刻的草稿'
            wait(); expect_count(0)
            host({'type': 'resumeStaging'}); wait(); expect_count(1)
        check('final close/handoff capture includes the latest draft and suspends pending sends', final_capture)

        def old_snapshot():
            reset(items=())
            host({'type': 'restoreStaging', 'generation': 1, 'state': None})
            status(); wait(); expect_count(0)
            assert queued() == [] and not page.locator('#stage').evaluate("e=>e.classList.contains('open')")
        check('older snapshots without staging data remain compatible', old_snapshot)
        browser.close()
finally:
    server.shutdown()

report = {'results': results, 'page_errors': errors, 'input_cases': input_cases,
          'passed': sum(r['passed'] for r in results), 'total': len(results)}
if args.report:
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(f"{report['passed']}/{report['total']} passed; {len(errors)} page errors. Offline fixtures only.")
raise SystemExit(0 if report['passed'] == report['total'] and not errors else 1)
