"""Offline clipboard/SSH replies against CC Pad's actual staging UI.

python tests/staged_images.py [--source <TerminalPane.xaml.cs>] [--report <json>]
Uses fictional image paths and a fake WebView host; never reads the clipboard,
opens a terminal, connects to SSH, or sends a real message.
"""
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
import argparse
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
A, B = '/tmp/ccpad-images/receipt-a.png', '/tmp/ccpad-images/receipt-b.png'

try:
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True, args=['--disable-gpu'])
        page = browser.new_page(viewport={'width': 1100, 'height': 900})
        page.on('pageerror', lambda error: errors.append(str(error)))
        box = page.locator('#stage-input')

        def host(msg): page.evaluate('msg=>__host(msg)', msg)
        def requests(): return page.evaluate("__sent.filter(m=>m.type==='stageImagePaste')")
        def queued(): return page.locator('#stage-list .txt').all_text_contents()
        def reset():
            page.goto(f'http://127.0.0.1:{server.server_port}/')
            page.wait_for_selector('.xterm-helper-textarea', state='attached')
            host({'type': 'paneStatus', 'status': 'working'})
            host({'type': 'setRemotePaste', 'on': True})
            host({'type': 'setStaging', 'on': True})
        def paste(target=box):
            before = len(requests())
            target.press('Alt+v')
            assert len(requests()) == before + 1, 'Image shortcut did not issue exactly one request'
            return requests()[-1]
        def reply(request, path=None, error=None):
            host({'type': 'stageImagePasted', 'requestId': request.get('requestId'), 'path': path, 'error': error})
        def check(name, action):
            try:
                reset(); action()
                result = {'case': name, 'passed': True}
            except Exception as error:
                result = {'case': name, 'passed': False, 'error': str(error)}
            results.append(result)
            print(('PASS ' if result['passed'] else 'FAIL ') + name)

        def sequential():
            reply(paste(), A); reply(paste(), B)
            box.press('Enter')
            assert queued() == [A + ' ' + B]
        check('two completed image pastes stay in one queued command', sequential)

        def early_enter():
            reply(paste(), A)
            second = paste(); box.press('Enter')
            assert queued() == [], 'Enter queued a partial two-image command'
            reply(second, B); box.press('Enter')
            assert queued() == [A + ' ' + B]
        check('Enter waits for the second image instead of splitting the draft', early_enter)

        def reversed_replies():
            first, second = paste(), paste()
            reply(second, B); reply(first, A); box.press('Enter')
            assert queued() == [A + ' ' + B], 'Images followed upload completion order'
        check('out-of-order uploads retain the original paste order', reversed_replies)

        def duplicate():
            request = paste(); reply(request, A); reply(request, A)
            host({'type': 'stageImagePasted', 'requestId': 'old-page-request', 'path': B})
            assert box.input_value().strip() == A, 'Duplicate or stale reply changed the draft'
        check('duplicate and stale replies cannot attach extra images', duplicate)

        def key_repeat():
            paste()
            box.dispatch_event('keydown', {'key': 'v', 'code': 'KeyV', 'altKey': True, 'repeat': True})
            assert len(requests()) == 1, 'A held shortcut re-read the clipboard'
        check('holding Alt+V cannot launch duplicate clipboard captures', key_repeat)

        def typing_during_upload():
            box.fill('核对'); box.press('End')
            request = paste()
            box.press('Home'); box.press('Shift+End')
            reply(request, A)
            assert box.input_value() == '核对 ' + A + ' ', 'Late image replaced newly selected text'
        check('a late image uses the original insertion point and preserves text', typing_during_upload)

        def edit_while_uploading():
            box.fill('核对'); box.press('End')
            request = paste()
            box.press('Home'); box.press('x')
            reply(request, A)
            assert box.input_value() == 'x核对 ' + A + ' '
        check('image insertion point follows edits made during upload', edit_while_uploading)

        def failed_second():
            reply(paste(), A)
            reply(paste(), error='fixture clipboard failure')
            assert box.input_value().strip() == A
            assert 'fixture clipboard failure' in page.locator('#stage-hint').inner_text()
            reply(paste(), B)
            assert 'fixture clipboard failure' not in page.locator('#stage-hint').inner_text()
            box.press('Enter'); assert queued() == [A + ' ' + B]
        check('failed second capture preserves the first and a successful retry clears the error', failed_second)

        def failure_then_success():
            first, second = paste(), paste()
            reply(second, B); reply(first, error='fixture first failed')
            assert box.input_value().strip() == B
            assert 'fixture first failed' in page.locator('#stage-hint').inner_text()
            box.press('Enter'); assert queued() == [B]
        check('one failed capture cannot block or hide another successful image', failure_then_success)

        def edit_existing():
            box.fill(A); box.press('Enter')
            page.locator('#stage-list .edit').click()
            editor = page.locator('#stage-list .edit-box')
            request = paste(editor); editor.press('Enter')
            assert editor.count() == 1, 'Editor saved before its image finished'
            reply(request, B); editor.press('Enter')
            assert queued() == [A + ' ' + B]
            assert box.input_value() == '', 'Image escaped into the new-command box'
        check('adding an image to an existing queued command targets its editor', edit_existing)

        def canceled_editor():
            box.fill(A); box.press('Enter')
            page.locator('#stage-list .edit').click()
            editor = page.locator('#stage-list .edit-box')
            request = paste(editor); editor.press('Escape'); reply(request, B)
            assert queued() == [A] and box.input_value() == ''
        check('canceling an edit discards its late image without modifying another draft', canceled_editor)

        def ctrl_v_image():
            box.evaluate('''el => {
                const data = new DataTransfer();
                data.items.add(new File(['fixture'], 'receipt.png', {type: 'image/png'}));
                el.dispatchEvent(new ClipboardEvent('paste', {clipboardData: data, bubbles: true, cancelable: true}));
            }''')
            assert len(requests()) == 1, 'Image-only Ctrl+V did not use the host bridge'
            reply(requests()[0], A); assert box.input_value().strip() == A
        check('image-only Ctrl+V uses the same protected staging flow', ctrl_v_image)

        def local_path():
            path = 'C:\\Users\\Fixture User\\Temp\\receipt.png'
            reply(paste(), path)
            assert box.input_value().strip() == '"' + path + '"'
        check('image paths containing spaces remain a single quoted reference', local_path)

        def reload():
            old = paste(); reset()
            current = paste(); reply(old, A); reply(current, B)
            assert box.input_value().strip() == B
        check('an upload from a previous page cannot attach to a new draft', reload)

        def blur_pending_editor():
            box.fill(A); box.press('Enter')
            page.locator('#stage-list .edit').click()
            editor = page.locator('#stage-list .edit-box')
            request = paste(editor); box.click()
            host({'type': 'setRemotePaste', 'on': False})
            host({'type': 'paneStatus', 'status': 'waiting'})
            page.wait_for_timeout(550)
            assert not page.evaluate("__sent.some(m=>m.type==='stagedInput')")
            reply(request, B)
            assert box.evaluate('el => document.activeElement === el'), 'Upload stole focus'
            editor.press('Enter')
            page.wait_for_timeout(550)
            messages = page.evaluate("__sent.filter(m=>m.type==='stagedInput')")
            assert len(messages) == 1 and messages[0]['data'] == A + ' ' + B
        check('blur cannot save or auto-send an editor until both images are ready', blur_pending_editor)

        def plain_paste():
            result = box.evaluate('''el => {
                const data = new DataTransfer(); data.setData('text/plain', 'receipt notes');
                const e = new ClipboardEvent('paste', {clipboardData: data, bubbles: true, cancelable: true});
                el.dispatchEvent(e); return e.defaultPrevented;
            }''')
            assert not result and requests() == [], 'Plain text paste was intercepted as an image'
        check('ordinary text paste retains native textarea behavior', plain_paste)

        def timed_out():
            page.clock.install()
            box.fill('receipt notes'); box.press('End')
            request = paste(); box.press('Enter'); assert queued() == []
            page.clock.fast_forward(60100)
            assert '超时' in page.locator('#stage-hint').inner_text()
            reply(request, A)
            host({'type': 'imgUploading', 'requestId': request.get('requestId')})
            assert box.input_value() == 'receipt notes'
            assert not page.locator('#paste-toast').is_visible()
            box.press('Enter'); assert queued() == ['receipt notes']
        check('missing host reply times out without trapping the draft or accepting a late image', timed_out)
        browser.close()
finally:
    server.shutdown()

report = {'results': results, 'page_errors': errors, 'passed': sum(r['passed'] for r in results), 'total': len(results)}
if args.report:
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(f"{report['passed']}/{report['total']} passed; {len(errors)} page errors. Offline fixtures only.")
raise SystemExit(0 if report['passed'] == report['total'] and not errors else 1)
