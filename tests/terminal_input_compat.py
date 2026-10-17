"""Regression tests using the actual bundled xterm and embedded HTML.

Run: python tests/terminal_input_compat.py --report <output.json>
Requires Python Playwright and its Chromium browser. No CCPad process, CLI,
native desktop, credentials, or PTY is opened. --baseline tests git HEAD.
"""
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
import argparse
import json
import re
import subprocess
import threading
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / 'CCPad' / 'Assets' / 'xterm'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--baseline', action='store_true')
parser.add_argument('--report', type=Path)
parser.add_argument('--assets', type=Path, help='Use an immutable published Assets/xterm directory for comparison')
args = parser.parse_args()
if args.assets: ASSETS = args.assets.resolve()

def source(path):
    if args.baseline:
        return subprocess.check_output(['git', 'show', 'HEAD:' + path], cwd=ROOT).decode('utf-8')
    return (ROOT / path).read_text(encoding='utf-8')

BRIDGE = """<script>
window.__inputs=[];window.__hostMessages=[];
window.chrome=window.chrome||{};
window.chrome.webview={listeners:[],
 addEventListener(type,fn){if(type==='message')this.listeners.push(fn)},
 postMessage(raw){const m=typeof raw==='string'?JSON.parse(raw):raw;
   __hostMessages.push(m);if(m.type==='input')__inputs.push(m.data);}};
class FixtureWebSocket {
 static OPEN=1;constructor(){this.readyState=1;queueMicrotask(()=>this.onopen?.())}
 send(raw){const m=JSON.parse(raw);if(m.type==='input')__inputs.push(m.data)}
 close(){this.readyState=3}
}
window.WebSocket=FixtureWebSocket;
</script>"""

local_code = source('CCPad/TerminalPane.xaml.cs')
local_html = re.search(r'private const string TerminalHtml = """\s*\n(.*?)\n\s*""";', local_code, re.S).group(1)
local_html = local_html.replace('https://xterm.local/', '/xterm/').replace('__INITIAL_DARK__','true').replace('__INITIAL_LASTCMD__','true')
local_html = local_html.replace('<head>', '<head>' + BRIDGE, 1)

web_code = source('CCPad/Web/WebTerminalHtml.cs')
web_html = re.search(r'=> \$\$"""\s*\n(.*?)\n""";', web_code, re.S).group(1)
def interpolate(match):
    expression=match.group(1)
    if expression == 'token': return 'fixture-only'
    if 'Loc.T' in expression: return 'label'
    if 'Cascadia' in expression: return "Consolas, monospace"
    if '16 : 14' in expression: return '16'
    if '1.25' in expression: return '1.25'
    if 'filter: brightness' in expression: return ''
    raise ValueError('Unrecognized interpolation: ' + expression)
web_html = re.sub(r'\{\{(.*?)\}\}', interpolate, web_html)
web_html = web_html.replace('<head>', '<head>' + BRIDGE, 1)

stock_html = '''<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="/xterm/xterm.css">
<div id="terminal" style="width:1000px;height:500px"></div>''' + BRIDGE + '''
<script src="/xterm/xterm.js"></script><script src="/xterm/ccpad-input-compat.js"></script>
<script>const term=new Terminal({cols:90,rows:12});term.open(document.getElementById('terminal'));
''' + ('' if args.baseline else 'term.loadAddon(new CCPadInputCompat(data=>__inputs.push(data)));') + '''
term.onData(data=>__inputs.push(data));term.focus();</script>'''
pages={'/local':local_html.encode(), '/web':web_html.encode(), '/stock':stock_html.encode()}

class Handler(BaseHTTPRequestHandler):
    def log_message(self,*unused): pass
    def do_GET(self):
        path=self.path.split('?',1)[0]
        if path in pages:
            data=pages[path];content_type='text/html'
        elif path == '/api/sessions':
            data=b'[]';content_type='application/json'
        elif path.startswith('/xterm/') and Path(path).name in {
            'xterm.js','xterm.css','xterm-addon-fit.js','ccpad-input-compat.js'}:
            file=ASSETS/Path(path).name;data=file.read_bytes()
            content_type='text/css' if file.suffix=='.css' else 'text/javascript'
        else:
            self.send_error(404);return
        self.send_response(200)
        self.send_header('Content-Type',content_type+'; charset=utf-8')
        self.send_header('Content-Length',str(len(data)))
        self.end_headers();self.wfile.write(data)

server=HTTPServer(('127.0.0.1',0),Handler)
thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
url=f'http://127.0.0.1:{server.server_port}'
results=[];page_errors=[]

try:
    with sync_playwright() as pw:
        browser=pw.chromium.launch(headless=True,args=['--disable-gpu'])
        page=browser.new_page(viewport={'width':1100,'height':750})
        page.on('pageerror',lambda error:page_errors.append(str(error)))
        cdp=page.context.new_cdp_session(page)
        def reset(lane):
            page.goto(url+'/'+lane)
            page.wait_for_selector('.xterm-helper-textarea',state='attached')
            page.locator('.xterm-helper-textarea').focus()
            page.wait_for_timeout(30)
        def state():
            page.wait_for_timeout(50)
            return page.evaluate("()=>({stream:__inputs.join(''), chunks:__inputs.slice(), textarea:term.textarea.value})")
        def check(lane,name,expected,extra=None):
            actual=state();ok=actual['stream']==expected
            if extra:ok=ok and extra(actual)
            results.append({'lane':lane,'case':name,'expected':expected,**actual,'passed':ok})
        def compose(text,replacement=None,settle=50):
            params={'text':text,'selectionStart':len(text),'selectionEnd':len(text)}
            if replacement is not None:params.update(replacementStart=replacement[0],replacementEnd=replacement[1])
            cdp.send('Input.imeSetComposition',params)
            cdp.send('Input.insertText',{'text':text})
            if settle:page.wait_for_timeout(settle)

        for lane in ['stock','local','web']:
            reset(lane)
            page.keyboard.type('abc123 ABC! ')
            cdp.send('Input.insertText',{'text':'中文测试'})
            check(lane,'physical keyboard plus committed Unicode','abc123 ABC! 中文测试')

            reset(lane);compose('abc')
            # Replace-style injectors inspect the focused edit field, not the PTY.
            residue=page.evaluate('term.textarea.value.length')
            compose('中文',(0,residue))
            check(lane,'replacement IME after already delivered abc','abc中文')

            reset(lane);page.keyboard.type(' A ');page.wait_for_timeout(50)
            residue=page.evaluate('term.textarea.value.length')
            compose('中文',(0,residue))
            check(lane,'replacement after capitals and spaces',' A 中文')

            reset(lane)
            for ch in 'abc123 中文':
                cdp.send('Input.dispatchKeyEvent',{'type':'rawKeyDown','key':ch,'code':'','windowsVirtualKeyCode':231})
                cdp.send('Input.dispatchKeyEvent',{'type':'char','text':ch,'unmodifiedText':ch,'key':ch,'windowsVirtualKeyCode':231})
                cdp.send('Input.dispatchKeyEvent',{'type':'keyUp','key':ch,'code':'','windowsVirtualKeyCode':231})
            check(lane,'Unicode packet character injection','abc123 中文')

            reset(lane)
            for text in ['a','中','🙂','文','a']:
                cdp.send('Input.insertText',{'text':text})
            check(lane,'text-only injection including emoji','a中🙂文a')

            for label,text,key in [
                ('first-character key','ab','a'),
                ('multi-character key','ab','ab'),
                ('unidentified key','ab','Unidentified'),
                ('Chinese character packet','中文','中'),
                ('emoji character packet','🙂','🙂'),
            ]:
                reset(lane)
                cdp.send('Input.dispatchKeyEvent',{'type':'char','text':text,'unmodifiedText':text,'key':key})
                check(lane,'complete character packet: '+label,text)

            for label,text,key in [('first key','ab','a'),('multi key','ab','ab'),('Chinese','中文','中')]:
                reset(lane)
                cdp.send('Input.dispatchKeyEvent',{'type':'rawKeyDown','key':key,'code':'','windowsVirtualKeyCode':231})
                cdp.send('Input.dispatchKeyEvent',{'type':'char','text':text,'unmodifiedText':text,'key':key,'windowsVirtualKeyCode':231})
                cdp.send('Input.dispatchKeyEvent',{'type':'keyUp','key':key,'code':'','windowsVirtualKeyCode':231})
                check(lane,'complete Unicode packet after keydown: '+label,text)

            reset(lane);compose('a')
            # A collapsed key describes the prior and current characters; its
            # legacy charCode repeats the first, while input.data is the new one.
            page.evaluate("""()=>{
              const ta=term.textarea;
              ta.dispatchEvent(new KeyboardEvent('keydown',{bubbles:true,cancelable:true,key:'ab',code:'KeyB',keyCode:66}));
              ta.dispatchEvent(new KeyboardEvent('keypress',{bubbles:true,cancelable:true,key:'ab',code:'KeyB',charCode:97,keyCode:97,which:97}));
              ta.value+='b';
              ta.dispatchEvent(new InputEvent('input',{bubbles:true,composed:true,data:'b',inputType:'insertText'}));
              ta.dispatchEvent(new KeyboardEvent('keyup',{bubbles:true,key:'ab',code:'KeyB',keyCode:66}));
            }""")
            check(lane,'collapsed key uses committed second character instead of repeating first','ab')

            reset(lane)
            page.evaluate("()=>new Promise(resolve=>term.write('\\x1b[?2004h',resolve))")
            cdp.send('Input.dispatchKeyEvent',{'type':'char','text':'ab','unmodifiedText':'ab','key':'a'})
            check(lane,'character packet stays raw with bracketed paste enabled','ab')

            reset(lane);page.evaluate('term.options.disableStdin=true')
            cdp.send('Input.dispatchKeyEvent',{'type':'char','text':'ab','unmodifiedText':'ab','key':'ab'})
            check(lane,'character packet respects disabled stdin','')

            for label,parts,code,legacy in [
                ('wrong legacy char and full Chinese commit',['你好'],'','甲'),
                ('wrong legacy char repeated for two commits',['你','好'],'','甲'),
                ('Unidentified physical code',['你好'],'Unidentified','甲'),
                ('reported wrong glyph 原 and full 你好 commit',['你好'],'','原'),
                ('reported 原原 from separate 你 and 好 commits',['你','好'],'','原'),
            ]:
                reset(lane)
                for text in parts:
                    page.evaluate("""({text,code,legacy})=>{
                      const ta=term.textarea;
                      const charCode=legacy.charCodeAt(0);
                      ta.dispatchEvent(new KeyboardEvent('keypress',{bubbles:true,cancelable:true,key:legacy,code,charCode,keyCode:charCode,which:charCode}));
                      ta.value+=text;
                      ta.dispatchEvent(new InputEvent('input',{bubbles:true,composed:true,data:text,inputType:'insertText'}));
                    }""",{'text':text,'code':code,'legacy':legacy})
                check(lane,'injected text prefers actual input.data: '+label,'你好')

            reset(lane)
            page.evaluate("""()=>new Promise(resolve=>{
              const ta=term.textarea;
              ta.dispatchEvent(new KeyboardEvent('keypress',{bubbles:true,cancelable:true,key:'甲',code:'',charCode:30002,keyCode:30002,which:30002}));
              setTimeout(()=>{
                ta.value='你好';
                ta.dispatchEvent(new InputEvent('input',{bubbles:true,composed:true,data:'你好',inputType:'insertText'}));
                resolve();
              },20);
            })""")
            check(lane,'delayed committed text does not repeat stale keypress character','你好')

            reset(lane)
            cdp.send('Input.dispatchKeyEvent',{'type':'rawKeyDown','key':'甲','code':'KeyN','windowsVirtualKeyCode':231})
            cdp.send('Input.dispatchKeyEvent',{'type':'char','text':'你好','unmodifiedText':'你好','key':'甲','windowsVirtualKeyCode':231})
            cdp.send('Input.dispatchKeyEvent',{'type':'keyUp','key':'甲','code':'KeyN','windowsVirtualKeyCode':231})
            check(lane,'Unicode packet does not trust inconsistent scan-code metadata','你好')

            reset(lane)
            page.evaluate("""()=>{
              const ta=term.textarea;
              ta.dispatchEvent(new KeyboardEvent('keydown',{bubbles:true,cancelable:true,key:'Process',code:'',keyCode:229}));
              ta.dispatchEvent(new KeyboardEvent('keypress',{bubbles:true,cancelable:true,key:'甲',code:'',charCode:30002,keyCode:30002,which:30002}));
              ta.value='你好';
              ta.dispatchEvent(new InputEvent('input',{bubbles:true,composed:true,data:'你好',inputType:'insertText'}));
              ta.dispatchEvent(new KeyboardEvent('keyup',{bubbles:true,key:'Process',keyCode:229}));
            }""")
            check(lane,'229 diff owns the commit instead of also emitting legacy keypress','你好')

            reset(lane);page.keyboard.type('abc');page.keyboard.press('Backspace');page.keyboard.type('d')
            check(lane,'backspace remains a terminal control','abc\x7fd')

            reset(lane)
            cdp.send('Input.imeSetComposition',{'text':'zhong','selectionStart':5,'selectionEnd':5})
            preview=state()
            cdp.send('Input.insertText',{'text':'中'})
            check(lane,'composition preview survives until commit','中',lambda unused:preview['stream']=='' and preview['textarea']=='zhong')

            reset(lane)
            cdp.send('Input.imeSetComposition',{'text':'cancel','selectionStart':6,'selectionEnd':6})
            cdp.send('Input.imeSetComposition',{'text':'','selectionStart':0,'selectionEnd':0})
            page.keyboard.type('a')
            check(lane,'cancelled composition sends no provisional text','a')

            reset(lane)
            for unused in range(20):
                compose('中文',settle=0)
            check(lane,'rapid consecutive composition commits','中文'*20)

            reset(lane)
            page.evaluate("""()=>{
              const ta=term.textarea;
              ta.dispatchEvent(new KeyboardEvent('keydown',{bubbles:true,cancelable:true,key:'Process',keyCode:229}));
              ta.value='123';
              ta.dispatchEvent(new InputEvent('input',{bubbles:true,composed:true,data:'123',inputType:'insertText'}));
              ta.dispatchEvent(new KeyboardEvent('keyup',{bubbles:true,key:'Process',keyCode:229}));
            }""")
            check(lane,'229 textarea diff completes before cleanup','123')

            reset(lane);page.evaluate("term.paste('hello中文\\nworld')")
            check(lane,'paste newline normalization','hello中文\rworld')

            reset(lane);page.keyboard.press('Control+c');page.keyboard.press('ArrowLeft');page.keyboard.press('Enter')
            check(lane,'control and navigation encoding','\x03\x1b[D\r')

            reset(lane);page.evaluate('term.options.screenReaderMode=true');compose('abc')
            check(lane,'screen reader textarea remains available','abc',lambda actual:actual['textarea']=='abc')

            reset(lane)
            page.evaluate("()=>{term.options.screenReaderMode=true;window.readerInputEvents=0;term.textarea.addEventListener('input',()=>readerInputEvents++);}")
            cdp.send('Input.dispatchKeyEvent',{'type':'char','text':'ab','unmodifiedText':'ab','key':'ab'})
            check(lane,'screen reader receives complete packet and native input event','ab',
                lambda actual:actual['textarea']=='ab' and page.evaluate('readerInputEvents')==1)

        # The user reports the same text is correct in the ordinary staging
        # textarea. Verify committed browser text is preserved there too.
        reset('local');page.evaluate("lastStatus='busy';applyStaging(true)")
        stage=page.locator('#stage-input');stage.focus()
        cdp.send('Input.insertText',{'text':'你好'})
        actual=page.evaluate('({queue:queue.slice(),value:stageInput.value})')
        results.append({'lane':'local','case':'reported 你好 is preserved in staging textarea',**actual,
            'passed':actual['queue']==[] and actual['value']=='你好'})

        reset('local')
        page.evaluate("lastStatus='busy';applyStaging(true)")
        stage=page.locator('#stage-input')
        stage.fill('候选文字')
        stage.evaluate("el=>el.dispatchEvent(new KeyboardEvent('keydown',{bubbles:true,cancelable:true,key:'Enter',keyCode:13,isComposing:true}))")
        actual=page.evaluate('({queue:queue.slice(),value:stageInput.value})')
        results.append({'lane':'local','case':'IME Enter does not enqueue staging text',**actual,
            'passed':actual['queue']==[] and actual['value']=='候选文字'})
        reset('local');page.evaluate("lastStatus='busy';applyStaging(true)")
        stage=page.locator('#stage-input');stage.fill('候选文字')
        stage.evaluate("el=>el.dispatchEvent(new KeyboardEvent('keydown',{bubbles:true,cancelable:true,key:'Enter',keyCode:229}))")
        actual=page.evaluate('({queue:queue.slice(),value:stageInput.value})')
        results.append({'lane':'local','case':'229 candidate confirmation does not enqueue',**actual,
            'passed':actual['queue']==[] and actual['value']=='候选文字'})
        # Ordinary Enter still queues once, after candidate confirmation has ended.
        reset('local');page.evaluate("lastStatus='busy';applyStaging(true)")
        stage=page.locator('#stage-input');stage.fill('候选文字')
        stage.evaluate("el=>el.dispatchEvent(new KeyboardEvent('keydown',{bubbles:true,cancelable:true,key:'Enter',keyCode:13}))")
        actual=page.evaluate('({queue:queue.slice(),value:stageInput.value})')
        results.append({'lane':'local','case':'ordinary staging Enter queues once',**actual,
            'passed':actual['queue']==['候选文字'] and actual['value']==''})

        report={'baseline':args.baseline,'assets':str(ASSETS),'engine':browser.version,'results':results,
            'page_errors':page_errors,'passed':sum(x['passed'] for x in results),'total':len(results),
            'limitation':'Controlled browser/IME event patterns. Not a capture of a physical phone remote session.'}
        if args.report:
            args.report.parent.mkdir(parents=True,exist_ok=True)
            args.report.write_text(json.dumps(report,ensure_ascii=True,indent=2),encoding='utf-8')
        for result in results:
            if not result['passed']:print(json.dumps(result,ensure_ascii=True))
        print(json.dumps({k:report[k] for k in ['baseline','engine','passed','total','page_errors']},ensure_ascii=True))
        browser.close()
        if report['passed']!=report['total'] or page_errors:raise SystemExit(1)
finally:
    server.shutdown();server.server_close()
