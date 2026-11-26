"""Offline gesture regressions against the desktop HTML and bundled xterm.

Uses real Chromium touch/mouse input and a fake WebView host. No live CCPad,
CLI, PTY, remote session or account is used. --source accepts a before snapshot.
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
parser.add_argument('--filter', default='', help='Run matching cases only')
args = parser.parse_args()
html = re.search(r'private const string TerminalHtml = """\s*\n(.*?)\n\s*""";',
                 args.source.read_text(encoding='utf-8'), re.S).group(1)
html = html.replace('https://xterm.local/', '/xterm/').replace('__INITIAL_DARK__', 'true').replace('__INITIAL_LASTCMD__', 'false')
html = html.replace('<head>', '''<head><script>
window.__sent=[];window.__listeners=[];window.chrome=window.chrome||{};
window.chrome.webview={
 addEventListener(t,f){if(t==='message')__listeners.push(f)},
 postMessage(raw){__sent.push(JSON.parse(raw))}};
window.__host=m=>__listeners.forEach(f=>f({data:JSON.stringify(m)}));
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
try:
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True, args=['--disable-gpu'],
                                     ignore_default_args=['--hide-scrollbars'])
        context = browser.new_context(viewport={'width': 1100, 'height': 850}, has_touch=True)
        page = context.new_page()
        cdp = context.new_cdp_session(page)
        page.on('pageerror', lambda e: errors.append(str(e)))

        def host(message): page.evaluate('m=>__host(m)', message)
        def write(text): page.evaluate('s=>new Promise(r=>term.write(s,r))', text)
        def inputs(): return page.evaluate("__sent.filter(m=>m.type==='input')")
        def viewport(): return page.evaluate('term.buffer.active.viewportY')

        def reset(mode='normal', remote=False):
            cdp.send('Emulation.setPageScaleFactor', {'pageScaleFactor': 1})
            page.goto(f'http://127.0.0.1:{server.server_port}/')
            page.wait_for_selector('.xterm-screen', state='visible')
            write(''.join(f'History line {i:04d} with selectable text\r\n' for i in range(400)))
            if mode.startswith('alternate'): write('\x1b[?1049h')
            if mode.endswith('mouse'): write('\x1b[?1002h\x1b[?1006h')
            if mode == 'normal': page.evaluate('term.scrollToLine(150)')
            host({'type': 'setRemoteScroll', 'on': remote})
            page.wait_for_timeout(60)
            page.evaluate('__sent=[]')
            page.evaluate('''()=>{
              window.__gestureTrace=[];
              for(const name of ['pointerdown','pointercancel','touchstart','touchmove','touchend'])
                window.addEventListener(name,e=>queueMicrotask(()=>__gestureTrace.push({
                  type:name, y:e.touches?.[0]?.clientY, target:e.target.className,
                  viewport:term.buffer.active.viewportY,
                  gesture:typeof terminalSwipe==='undefined'?null:terminalSwipe && {
                    cancelled:terminalSwipe.cancelled, startY:terminalSwipe.startY,
                    lastY:terminalSwipe.lastY, partial:terminalSwipe.partialLines}})),true);
            }''')

        def touch_event(kind, points):
            cdp.send('Input.dispatchTouchEvent', {'type': kind, 'touchPoints': [
                {'x': x, 'y': y, 'id': i + 1, 'radiusX': 2, 'radiusY': 2}
                for i, (x, y) in enumerate(points)]})

        def touch_drag(dy=120, fingers=1, x=280, y=300):
            start = [(x + i * 130, y) for i in range(fingers)]
            touch_event('touchStart', start)
            for step in range(1, 13):
                touch_event('touchMove', [(px, py + dy * step / 12) for px, py in start])
            touch_event('touchEnd', [])
            page.wait_for_timeout(80)

        def mouse_drag(x=280, y=300, dx=0, dy=120):
            page.mouse.move(x, y)
            page.mouse.down()
            page.mouse.move(x + dx, y + dy, steps=12)
            page.mouse.up()
            page.wait_for_timeout(70)

        def check(name, action):
            if args.filter not in name: return
            try:
                action()
                result = {'case': name, 'passed': True}
            except Exception as e:
                result = {'case': name, 'passed': False, 'error': str(e)}
                result['diagnostic'] = page.evaluate('''()=>({
                  trace:window.__gestureTrace, inputs:__sent.filter(m=>m.type==='input'),
                  viewport:term.buffer.active.viewportY, font:term.options.fontSize,
                  selection:term.getSelection(), touchAction:getComputedStyle(document.querySelector('.xterm-screen')).touchAction,
                  scale:visualViewport.scale,
                  ancestors:(()=>{let a=[],e=document.elementFromPoint(280,300);while(e){
                    const s=getComputedStyle(e);a.push([e.tagName,e.className,s.touchAction,s.overflowY]);e=e.parentElement;}return a;})()})''')
            results.append(result)
            print(('PASS ' if result['passed'] else 'FAIL ') + name +
                  (': ' + result['error'] if not result['passed'] else ''), flush=True)

        def normal_touch(fingers):
            reset()
            before = viewport()
            touch_drag(fingers=fingers)
            after = viewport()
            line_height = page.locator('.xterm-rows > div').first.bounding_box()['height']
            expected = int(120 / line_height)
            assert abs((before - after) - expected) <= 1, (before, after, expected)
            touch_drag(dy=-120, fingers=fingers)
            assert abs(viewport() - before) <= 1, (before, viewport())
            assert not inputs(), inputs()
            assert page.evaluate('term.getSelection()') == ''
            assert not page.evaluate("__sent.some(m=>['copy','openLink','contextMenu'].includes(m.type))")
        for fingers in (1, 2):
            check(f'{fingers}-finger normal history scrolls once in both directions', lambda n=fingers: normal_touch(n))

        def mouse_protocol(mode, fingers):
            reset(mode)
            touch_drag(fingers=fingers)
            reports = inputs()
            assert reports, 'No wheel reports reached the CLI'
            assert all(re.fullmatch(r'\x1b\[<64;\d+;\d+M', m['data']) for m in reports), reports
            assert all(m['source'] == 'wheel-scroll' for m in reports), reports
            assert all(';1;1M' not in m['data'] for m in reports), reports
            page.evaluate('__sent=[]')
            touch_drag(dy=-120, fingers=fingers)
            assert inputs() and all(re.fullmatch(r'\x1b\[<65;\d+;\d+M', m['data']) for m in inputs()), inputs()
        for mode in ('normal-mouse', 'alternate-mouse'):
            for fingers in (1, 2):
                check(f'{mode}: {fingers}-finger pan sends wheel reports without clicks',
                      lambda m=mode, n=fingers: mouse_protocol(m, n))

        def alternate_arrows():
            reset('alternate')
            touch_drag()
            assert inputs() and all(m['data'] in ('\x1b[A', '\x1bOA') and
                                   m['source'] == 'wheel-scroll' for m in inputs()), inputs()
            assert page.evaluate('shadowBuf') == ''
        check('alternate buffer without mouse uses navigation arrows without editing shadow input', alternate_arrows)

        def remote_pan():
            reset(remote=True)
            before = viewport()
            mouse_drag()
            assert viewport() < before, (before, viewport())
            assert page.evaluate('term.getSelection()') == ''
            assert not page.evaluate("__sent.some(m=>['input','copy','openLink'].includes(m.type))")
            mouse_drag(dy=-120)
            assert abs(viewport() - before) <= 1, (before, viewport())
        check('remote mouse drag is opt-in and follows the finger without selecting/copying', remote_pan)

        def select_text(toggle_off=False):
            reset(remote=toggle_off)
            if toggle_off: host({'type': 'setRemoteScroll', 'on': False})
            mouse_drag(x=20, y=80, dx=240, dy=0)
            assert page.evaluate('term.getSelection()'), 'Mouse text selection lost'
            assert page.evaluate("__sent.some(m=>m.type==='copy')"), 'Copy-on-select lost'
        check('ordinary mouse still selects and copies with mode off', select_text)
        check('turning remote mode off restores text selection', lambda: select_text(True))

        def remote_mouse_protocol():
            reset('alternate-mouse', remote=True)
            mouse_drag()
            assert inputs() and all(m['data'].startswith('\x1b[<64;') for m in inputs()), inputs()
        check('remote drag in full-screen CLI emits wheel reports only', remote_mouse_protocol)

        def cell_point(col, row):
            return page.evaluate('''([col,row])=>{
              const r=document.querySelector('.xterm-screen').getBoundingClientRect();
              return {x:r.left+(col-.5)*r.width/term.cols,y:r.top+(row-.5)*r.height/term.rows};
            }''', [col, row])

        def link_tap(native):
            reset(remote=True)
            write('\x1b[2J\x1b[Hhttps://example.test/touch\r\n')
            page.evaluate('term.scrollToBottom()')
            page.wait_for_timeout(60)
            p = cell_point(8, 1)
            # Move the desktop cursor away: touch taps must resolve their own link.
            page.mouse.move(1000, 800)
            page.evaluate('__sent=[]')
            if native:
                touch_event('touchStart', [(p['x'], p['y'])])
                touch_event('touchEnd', [])
            else:
                page.mouse.click(**p)
            page.wait_for_timeout(80)
            links = page.evaluate("__sent.filter(m=>m.type==='openLink').map(m=>m.url)")
            assert links == ['https://example.test/touch'], links
        check('touch tap opens the intended link once', lambda: link_tap(True))
        check('remote-mode mouse tap opens the intended link once', lambda: link_tap(False))

        def no_accidental_link():
            reset(remote=True)
            write('\x1b[2J\x1b[Hhttps://example.test/touch\r\n')
            page.evaluate('term.scrollToBottom()')
            page.wait_for_timeout(60)
            p = cell_point(8, 1)
            mouse_drag(x=p['x'], y=p['y'])
            assert not page.evaluate("__sent.some(m=>m.type==='openLink')")
        check('swiping over a link does not open it', no_accidental_link)

        def pinch():
            reset('alternate-mouse')
            touch_event('touchStart', [(300, 300), (500, 300)])
            for i in range(1, 11):
                touch_event('touchMove', [(300 - i * 8, 300 - i * 2), (500 + i * 8, 300 + i * 2)])
            touch_event('touchEnd', [])
            page.wait_for_timeout(80)
            assert not inputs(), inputs()
            assert page.locator('.xterm-screen').evaluate("e=>getComputedStyle(e).touchAction") == 'pinch-zoom'
            assert page.evaluate('visualViewport.scale') > 1, 'Native pinch zoom did not zoom'
            # Remote clients commonly translate pinch into Ctrl+wheel. Preserve
            # that existing font zoom path too, without sending navigation.
            reset('alternate-mouse')
            before = page.evaluate('term.options.fontSize')
            page.mouse.move(400, 400)
            page.keyboard.down('Control')
            page.mouse.wheel(0, -100)
            page.keyboard.up('Control')
            page.wait_for_timeout(80)
            assert page.evaluate('term.options.fontSize') > before, (before, page.evaluate('term.options.fontSize'))
            assert not inputs(), inputs()
        check('pinch does not become pan and existing Ctrl+wheel zoom is preserved', pinch)

        def controls_and_scrollbar():
            reset(remote=True)
            before = viewport()
            host({'type': 'setStaging', 'on': True})
            editor = page.locator('#stage-input')
            editor.fill('still editable')
            assert editor.input_value() == 'still editable'
            assert viewport() == before
            host({'type': 'setStaging', 'on': False})
            page.evaluate('term.scrollToBottom()')
            page.wait_for_timeout(60)
            r = page.locator('.xterm-viewport').bounding_box()
            mouse_drag(x=r['x'] + r['width'] - 5, y=r['y'] + r['height'] - 15, dy=-240)
            assert viewport() < page.evaluate('term.buffer.active.baseY') - 20, viewport()
        check('staging editor and native scrollbar remain usable in remote mode', controls_and_scrollbar)

        def cancel():
            reset(remote=True)
            page.mouse.move(280, 300)
            page.mouse.down()
            page.mouse.move(280, 360, steps=4)
            page.evaluate("window.dispatchEvent(new Event('blur'))")
            before = viewport()
            page.mouse.move(280, 450, steps=4)
            page.mouse.up()
            page.wait_for_timeout(80)
            assert viewport() == before
            touch_event('touchStart', [(280, 300)])
            touch_event('touchCancel', [])
            assert not page.evaluate("document.querySelector('#terminal').classList.contains('swipe-scrolling')")
            touch_drag()
            assert viewport() < before
        check('blur/touch cancellation stops scrolling and the next gesture still works', cancel)

        def wheel_and_middle():
            reset(remote=True)
            before = viewport()
            page.mouse.move(280, 300)
            page.mouse.wheel(0, 120)
            page.wait_for_timeout(80)
            assert viewport() > before, ('wheel', before, viewport())
            before = viewport()
            page.mouse.click(280, 300, button='middle')
            page.mouse.move(280, 440)
            page.wait_for_timeout(300)
            page.mouse.click(280, 440, button='middle')
            assert viewport() > before, ('middle', before, viewport())
        check('ordinary wheel and middle-button scrolling still work', wheel_and_middle)

        browser.close()
finally:
    server.shutdown()
report = {'checks': results, 'pageErrors': errors,
          'passed': sum(r['passed'] for r in results), 'total': len(results),
          'liveRemoteClientTested': False}
if args.report:
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(f"{report['passed']}/{report['total']} passed; {len(errors)} page errors", flush=True)
raise SystemExit(0 if all(r['passed'] for r in results) and not errors else 1)
