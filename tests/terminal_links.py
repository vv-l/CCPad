"""Click actual bundled xterm links in the embedded desktop frontend, offline.

python tests/terminal_links.py [--source <TerminalPane.xaml.cs>] [--report <json>]
Uses fictional output and a fake WebView host. No SSH, PTY, external navigation,
or native browser launch. --source supports comparison with the pre-fix frontend.
"""
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
import argparse
import json
import re
import threading
import unicodedata
from urllib.parse import quote
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', type=Path, default=ROOT / 'CCPad/TerminalPane.xaml.cs')
parser.add_argument('--report', type=Path)
parser.add_argument('--screenshots', type=Path, help='Save offline hover alignment previews')
args = parser.parse_args()
source = args.source.read_text(encoding='utf-8')
html = re.search(r'private const string TerminalHtml = """\s*\n(.*?)\n\s*""";', source, re.S).group(1)
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

try:
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True, args=['--disable-gpu'])
        page = browser.new_page(viewport={'width': 1400, 'height': 850})
        page.on('pageerror', lambda error: errors.append(str(error)))

        def reset(text, cols=100, mouse=False):
            page.goto(f'http://127.0.0.1:{server.server_port}/')
            page.wait_for_selector('.xterm-helper-textarea', state='attached')
            page.evaluate('cols=>{fit.fit=()=>{};term.resize(cols,24)}', cols)
            if mouse:
                text = '\x1b[?1049h\x1b[?1002h\x1b[?1006h' + text
            page.evaluate('text=>new Promise(resolve=>term.write(text,resolve))', text)
            page.wait_for_timeout(60)

        def point(x, y):
            return page.evaluate('''([x,y])=>{
              const rect=document.querySelector('.xterm-screen').getBoundingClientRect();
              return {x:rect.left+(x-0.5)*rect.width/term.cols,
                      y:rect.top+(y-0.5)*rect.height/term.rows};
            }''', [x, y])

        def click(x, y, expected, button='left', shift=False):
            page.evaluate('__sent=[]')
            page.mouse.move(1390, 840)
            # Traverse a different terminal cell as a real mouse would. xterm
            # caches the last hovered cell even after a synthetic mouse leave.
            page.mouse.move(**point(1, 23))
            pos = point(x, y)
            page.mouse.move(**pos)
            if shift: page.keyboard.down('Shift')
            page.mouse.click(**pos, button=button)
            if shift: page.keyboard.up('Shift')
            actual = page.evaluate("__sent.filter(m=>m.type==='openLink').map(m=>m.url)")
            assert actual == ([] if expected is None else [expected]), f'cell {x},{y}: {actual!r}, expected {expected!r}'

        def check(name, action):
            try:
                action()
                result = {'case': name, 'passed': True}
            except Exception as error:
                result = {'case': name, 'passed': False, 'error': str(error)}
                result['diagnostic'] = page.evaluate('''()=>({
                  cells:Array.from({length:14},(_,x)=>{
                    const c=term.buffer.active.getLine(term.buffer.active.viewportY).getCell(x);
                    return [c.getChars(),c.getWidth()];}),
                  selection:term.getSelection(), mouse:term.modes.mouseTrackingMode
                })''')
            results.append(result)
            print(('PASS ' if result['passed'] else 'FAIL ') + name, flush=True)

        url = 'http://jira.example.test/browse/AINAS-14294'

        def jira():
            reset('\x1b[4;1HAINAS-14294 (\x1b[4;34m' + url + '\x1b[0m)', mouse=True)
            for x in (14, 25, 13 + len(url)): click(x, 4, url)
            click(13, 4, None)
            click(14 + len(url), 4, None)
        check('remote mouse mode: parenthesized Jira URL and exact boundaries', jira)

        def hover_alignment():
            reset('\x1b[2;1HOriginal issue\x1b[3;1H' + '─' * 65 +
                  '\x1b[4;1HAINAS-14294 (\x1b[34m' + url + '\x1b[0m)' +
                  '\x1b[5;1H' + '─' * 65 + '\x1b[7;1H', mouse=True)
            page.evaluate('term.options.fontSize=18')
            page.wait_for_timeout(60)

            def hover(x, y, name):
                page.mouse.move(**point(1, 23))
                page.mouse.move(**point(x, y))
                page.evaluate('()=>new Promise(requestAnimationFrame)')
                if args.screenshots:
                    args.screenshots.mkdir(parents=True, exist_ok=True)
                    page.screenshot(path=str(args.screenshots / (name + '.png')),
                                    clip={'x': 0, 'y': 0, 'width': 900, 'height': 190})
                return page.evaluate('''()=>({
                  pointer:!!document.querySelector('#terminal .xterm-cursor-pointer'),
                  underlines:[...document.querySelectorAll('.xterm-rows > div')]
                    .flatMap((row,y)=>[...row.querySelectorAll('span')]
                      .filter(span=>getComputedStyle(span).textDecorationLine.includes('underline'))
                      .map(span=>({row:y+1,text:span.textContent})))
                })''')

            above = hover(20, 3, 'separator-hover')
            on_url = hover(20, 4, 'url-hover')
            assert above == {'pointer': False, 'underlines': []}, f'separator hover: {above}'
            assert on_url['pointer'], 'URL hover has no pointer cursor'
            assert {part['row'] for part in on_url['underlines']} == {4}, on_url
            assert ''.join(part['text'] for part in on_url['underlines']) == url, on_url
            after = hover(1, 6, 'after-leave')
            assert after == {'pointer': False, 'underlines': []}, after
        check('hover underline covers only the URL row, never the separator above it', hover_alignment)

        def adjacent():
            first, second = 'https://first.example.test/a', 'https://second.example.test/b'
            reset(first + '\r\n' + second)
            click(8, 1, first)
            click(8, 2, second)
            click(8, 3, None)
        check('links on adjacent rows open the clicked row, including row one', adjacent)

        def wide():
            reset('中文 e\u0301 😀 ' + url)
            # Bundled xterm's Unicode 6 table gives this emoji one cell,
            # despite its two UTF-16 units; the combining cluster is one too.
            click(10, 1, url)
            click(9 + len(url), 1, url)
            click(9, 1, None)
        check('Chinese, combining marks and emoji before a URL keep cell alignment', wide)

        def wrapped():
            prefix = '中文 ' + 'x' * 37 + ' '
            reset(prefix + url, cols=50)
            click(44, 1, url)
            click(1, 2, url)
            click(43 + len(url) - 50, 2, url)
            click(44 + len(url) - 50, 2, None)
        check('soft-wrapped URL is clickable on both rows with wide-character prefix', wrapped)

        def spaces():
            reset('x' * 48 + '  ' + url, cols=50)
            click(1, 2, url)
            click(40, 2, url)
            click(49, 1, None)
        check('spaces at a wrapped row boundary preserve URL coordinates', spaces)

        def separate():
            reset('https://a.example.test/\r\ncontinued')
            click(5, 1, 'https://a.example.test/')
            click(5, 2, None)
        check('hard line breaks never join unrelated text into a URL', separate)

        panel = 'https://192.0.2.167:8443/automation/panel/1.14.0版本测试面板.html?'
        panel_tail = 'case=AINAS-14294'
        panel_target = quote(panel + panel_tail, safe=':/?=&%#')

        def cell_width(text):
            return sum(2 if unicodedata.east_asian_width(c) in ('W', 'F') else 1 for c in text)

        prose_prefix = '  HTML 已升到 0.26，链接没变：'
        prose_head = 'https://example-global.fe'
        prose_tail = 'ishu.cn/sheets/RegressionExample123'
        prose_url = prose_head + prose_tail
        prose_cols = cell_width(prose_prefix + prose_head)

        def prose_text(parts, prefix=prose_prefix, indent='  ', style='1', tail_style=None,
                       suffix='。 我读回核对过，8 项都在。'):
            output = ''
            for index, part in enumerate(parts):
                output += f'\x1b[{4 + index};1H' + (prefix if index == 0 else indent)
                output += '\x1b[' + (style if index == 0 or tail_style is None else tail_style) + 'm'
                output += part + '\x1b[0m'
                if index == len(parts) - 1: output += suffix
            return output

        def prose_domain():
            for margin in (0, 1, 2):
                for style in ('0', '1'):
                    reset(prose_text([prose_head, prose_tail], style=style),
                          cols=prose_cols + margin, mouse=True)
                    assert not page.evaluate('term.buffer.active.getLine(4).isWrapped')
                    click(cell_width(prose_prefix) + 1, 4, prose_url)
                    click(prose_cols, 4, prose_url)
                    click(3, 5, prose_url)
                    click(2 + len(prose_tail), 5, prose_url)
        check('prose hard wrap: split domain opens the complete URL from either fragment', prose_domain)

        def prose_boundaries():
            reset(prose_text([prose_head, prose_tail]), cols=prose_cols + 1, mouse=True)
            for x, y in ((cell_width(prose_prefix), 4), (prose_cols + 1, 4), (2, 5),
                         (3 + len(prose_tail), 5), (4 + len(prose_tail), 5)):
                click(x, y, None)
            click(4, 5, prose_url)
            if args.screenshots:
                args.screenshots.mkdir(parents=True, exist_ok=True)
                page.screenshot(path=str(args.screenshots / 'prose-tail-hover.png'),
                                clip={'x': 0, 'y': 0, 'width': 900, 'height': 190})
        check('prose hard wrap: indentation, row padding and following Chinese prose stay inactive', prose_boundaries)

        def prose_three_rows():
            first = 'https://example.test/path?'
            prefix = '  链接：'
            cols = cell_width(prefix + first)
            middle = 'q=' + 'x' * (cols - 4)
            tail = '&next=%E4%B8%AD#result'
            reset(prose_text([first, middle, tail], prefix=prefix), cols=cols)
            for x, y in ((cell_width(prefix) + 1, 4), (3, 5), (cols, 5), (3, 6)):
                click(x, y, first + middle + tail)
        check('prose hard wrap: three rows retain query separator, escaped Unicode and fragment', prose_three_rows)

        def prose_reject():
            for options, cols in (({}, prose_cols + 8), ({'indent': '    '}, prose_cols),
                                  ({'tail_style': '31'}, prose_cols),
                                  ({'tail_style': '0'}, prose_cols)):
                reset(prose_text([prose_head, prose_tail], **options), cols=cols)
                click(cell_width(prose_prefix) + 1, 4, prose_head + '/')
                click(5, 5, None)
            for tail in ('https://other.example.test/', '───', '', '● next item'):
                reset(prose_text([prose_head, tail]), cols=prose_cols)
                click(cell_width(prose_prefix) + 1, 4, prose_head + '/')
        check('prose recovery rejects short lines, changed indent/style, new URLs and separators', prose_reject)

        def prose_closed():
            for ending in (').', '。', ', '):
                first = 'https://example.test/path' + ending
                reset(prose_text([first, 'unrelated'], prefix='  ('),
                      cols=cell_width('  (' + first))
                click(4, 4, 'https://example.test/path')
                click(3, 5, None)
        check('prose recovery does not continue a URL already followed by closing punctuation', prose_closed)

        def prose_scrolling():
            reset(prose_text([prose_head, prose_tail]) + '\x1b[24;1H\r\n',
                  cols=prose_cols, mouse=True)
            click(cell_width(prose_prefix) + 1, 3, prose_url)
            click(3, 4, prose_url)
        check('prose hard wrap: recovered ranges follow terminal scrolling', prose_scrolling)

        def table_text(parts, left=3, row=4, close=True, tail_style='4;34'):
            prefix = 'AINAS-14294 ('
            output = ''
            for index, part in enumerate(parts):
                output += f'\x1b[{row + index};{left}H'
                if index == 0: output += prefix
                style = '4;34' if index == 0 else tail_style
                output += f'\x1b[{style}m' + part + '\x1b[0m'
                if index == len(parts) - 1 and close: output += ')'
                output += f'\x1b[{row + index};115H右侧问题说明'
            return output

        def hard_table_head():
            reset(table_text([panel, panel_tail]), cols=140, mouse=True)
            assert not page.evaluate('term.buffer.active.getLine(4).isWrapped')
            click(3 + len('AINAS-14294 ('), 4, panel_target)
            click(2 + len('AINAS-14294 (') + cell_width(panel), 4, panel_target)
        check('table hard wrap: first fragment opens complete Unicode URL including query', hard_table_head)

        def hard_table_tail():
            reset(table_text([panel, panel_tail]), cols=140, mouse=True)
            click(3, 5, panel_target)
            click(2 + len(panel_tail), 5, panel_target)
            for x, y in ((2, 5), (3 + len(panel_tail), 5), (100, 4), (115, 4), (115, 5)):
                click(x, y, None)
            if args.screenshots:
                args.screenshots.mkdir(parents=True, exist_ok=True)
                page.mouse.move(**point(4, 5))
                page.evaluate('()=>new Promise(requestAnimationFrame)')
                page.screenshot(path=str(args.screenshots / 'table-tail-hover.png'),
                                clip={'x': 0, 'y': 0, 'width': 1300, 'height': 170})
            page.mouse.move(**point(115, 4))
            assert not page.evaluate("!!document.querySelector('#terminal .xterm-cursor-pointer')")
        check('table hard wrap: continuation opens full URL, padding and other column stay inactive', hard_table_tail)

        def hard_three_rows():
            parts = [panel[:-1], '?case=AINAS-14294&view=', '详情#result']
            target = quote(''.join(parts), safe=':/?=&%#')
            reset(table_text(parts), cols=140)
            for x, y in ((18, 4), (3, 5), (3, 6), (4, 6), (13, 6)):
                click(x, y, target)
        check('three hard-wrapped fragments preserve Unicode, query separators and fragment', hard_three_rows)

        def hard_second_column():
            reset(table_text(['https://example.test/a_(one)?', 'case=two#result'], left=45) +
                  '\x1b[4;3HUnrelated first column\x1b[5;3HStill unrelated', cols=140)
            target = 'https://example.test/a_(one)?case=two#result'
            click(58, 4, target)
            click(45, 5, target)
            click(3, 5, None)
        check('wrapped link in a later table column preserves balanced URL parentheses', hard_second_column)

        def hard_unrelated():
            for tail, style, close in ((panel_tail, '4;31', True),
                                       (panel_tail, '34', True),
                                       ('https://other.example.test/', '4;34', True),
                                       (panel_tail, '4;34', False)):
                reset(table_text([panel, tail], tail_style=style, close=close), cols=140)
                click(18, 4, quote(panel[:-1], safe=':/?=&%#'))
            reset(table_text([panel[:-1]], close=True) +
                  '\x1b[5;3H\x1b[4;34mcase=unrelated\x1b[0m)', cols=140)
            click(18, 4, quote(panel[:-1], safe=':/?=&%#'))
            click(3, 5, None)
        check('hard-wrap recovery rejects unrelated style, new URL, incomplete or already closed wrapper', hard_unrelated)

        def hard_boundaries():
            first = 'https://example.test/?'
            tail = 'q=' + 'x' * (len('AINAS-14294 (') + len(first) - 2)
            reset(table_text([first, tail]), cols=140)
            click(3, 5, first + tail)
            click(2 + len(tail), 5, first + tail)
            click(3 + len(tail), 5, None)
            # Closing punctuation can occupy its own rendered table row.
            reset(table_text([first, 'q=yes!', '']), cols=140)
            click(18, 4, first + 'q=yes!')
            click(3, 5, first + 'q=yes!')
            click(3, 6, None)
        check('full-width continuation and separate closing bracket keep exact boundaries and punctuation', hard_boundaries)

        def hard_separation():
            for interruption in ('\x1b[5;3H' + '─' * 60,
                                 '\x1b[5;3H\x1b[4;34mAINAS-15447\x1b[0m (another item)',
                                 '\x1b[5;2H\x1b[4;34mxcase=unrelated\x1b[0m)'):
                reset(table_text([panel, panel_tail]) + interruption, cols=140)
                click(18, 4, quote(panel[:-1], safe=':/?=&%#'))
        check('separators, neighboring labels and misaligned tokens cannot become URL continuations', hard_separation)

        def hard_scrollback():
            reset(table_text([panel, panel_tail]) + '\x1b[24;1H\r\n', cols=140, mouse=True)
            # Alternate buffer scrolls by one physical row, without soft wraps.
            click(18, 3, panel_target)
            click(3, 4, panel_target)
        check('hard-wrapped table ranges follow buffer scrolling', hard_scrollback)

        def scrollback():
            reset(('filler\r\n' * 30) + url)
            click(1, 24, url)
            page.evaluate('term.scrollLines(-1)')
            click(1, 24, None)
            page.evaluate('term.scrollToBottom()')
            click(20, 24, url)
        check('buffer coordinates remain correct after scrollback', scrollback)

        def punctuation():
            balanced = 'https://example.test/Topic_(one)?q=(two)#part'
            reset('查看（' + balanced + '）。')
            click(7, 1, balanced)
            click(7 + len(balanced), 1, None)
        check('Chinese delimiters are excluded while balanced URL parentheses survive', punctuation)

        def osc():
            target = 'https://example.test/Topic_(one)?q=yes!'
            reset('\x1b]8;;' + target + '\x1b\\中文链接\x1b]8;;\x1b\\', mouse=True)
            click(1, 1, target)
            click(8, 1, target)
        check('OSC-8 labels retain their explicit destination in remote mouse mode', osc)

        def selection():
            reset(url)
            page.mouse.move(**point(3, 1))
            page.mouse.down()
            page.mouse.move(**point(15, 1), steps=12)
            page.mouse.up()
            assert page.evaluate('term.hasSelection()'), 'drag did not select text'
            assert page.evaluate("__sent.filter(m=>m.type==='openLink').length") == 0
        check('dragging to select link text does not open the browser', selection)

        def gestures():
            for button in ('right', 'middle'):
                reset(url, mouse=True)
                click(5, 1, None, button=button)
            reset(url, mouse=True)
            click(5, 1, None, shift=True)
            click(5, 1, url)
        check('context menu, middle scroll and Shift-selection do not launch links', gestures)

        def previous_selection():
            reset(url, mouse=True)
            page.evaluate('term.select(0,0,4)')
            click(10, 1, url)
        check('an old remote text selection does not disable a new link click', previous_selection)

        def unicode_url():
            raw = 'https://example.test/' + 'x' * 9 + '中文?q=ok'
            expected = 'https://example.test/' + 'x' * 9 + '%E4%B8%AD%E6%96%87?q=ok'
            reset(raw, cols=30)
            click(1, 1, expected)
            click(1, 2, expected)
            click(2, 2, expected)
            click(9, 2, expected)
        check('wide URL character wrapping over the final spare cell stays intact', unicode_url)

        def same_row():
            first, second = 'https://a.example.test/a_(b)', 'http://[::1]:8080/path'
            reset('(' + first + ').  [' + second + '].')
            click(2, 1, first)
            click(len(first) + 7, 1, second)
            click(len(first) + 2, 1, None)
        check('multiple links retain balanced parentheses and IPv6 hosts', same_row)

        def unsafe():
            reset('\x1b]8;;file:///C:/Windows/test\x1b\\unsafe\x1b]8;;\x1b\\\r\n'
                  'https://user:secret@example.test/path')
            click(2, 1, None)
            click(10, 2, None)
        check('non-web and credential-bearing destinations never reach the host', unsafe)

        report = {'results': results, 'passed': sum(r['passed'] for r in results),
                  'total': len(results), 'pageErrors': errors, 'engine': browser.version,
                  'limitation': 'Offline xterm mouse clicks and host messages; no live SSH or browser launch.'}
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(report, ensure_ascii=False, indent=2))
        browser.close()
finally:
    server.shutdown()

raise SystemExit(0 if all(r['passed'] for r in results) and not errors else 1)
