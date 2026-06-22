namespace CCPad.Web
{
    internal static class WebTerminalHtml
    {
        public static string GetHtml(string token) => $$"""
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, minimum-scale=1, user-scalable=no, viewport-fit=cover"/>
  <meta name="apple-mobile-web-app-capable" content="yes"/>
  <meta name="mobile-web-app-capable" content="yes"/>
  <meta name="format-detection" content="telephone=no"/>
  <title>CC Pad — Remote Terminal</title>
  <style>
    * { margin: 0; padding: 0; box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
    html { touch-action: manipulation; height: 100%; }
    html, body {
      width: 100%; height: 100%;
      background: #0c0c0c; overflow: hidden;
      font-family: 'Segoe UI', sans-serif;
      overscroll-behavior: none;
    }
    /* Use dvh for mobile browsers with dynamic toolbars */
    @supports (height: 100dvh) {
      html, body { height: 100dvh; }
    }

    #app { display: flex; width: 100%; height: 100%; overflow: hidden; }

    /* Sidebar */
    #sidebar {
      width: 220px; min-width: 220px; height: 100%;
      background: #181818; border-right: 1px solid #333;
      display: flex; flex-direction: column;
      transition: margin-left 0.2s; flex-shrink: 0;
    }
    #sidebar.collapsed { margin-left: -220px; }
    #sidebar-header {
      padding: 12px 14px; font-size: 13px; font-weight: 600; color: #ccc;
      border-bottom: 1px solid #333;
      display: flex; justify-content: space-between; align-items: center;
    }
    #sidebar-header button {
      background: none; border: none; color: #888; cursor: pointer; font-size: 16px; padding: 2px 6px;
    }
    #sidebar-header button:hover { color: #fff; }
    #session-list { flex: 1; overflow-y: auto; padding: 6px 0; }
    .session-item {
      padding: 10px 14px; cursor: pointer; border-left: 3px solid transparent;
      transition: background 0.15s;
    }
    .session-item:hover { background: #252525; }
    .session-item.active { background: #333; border-left-color: #888; }
    .session-label { font-size: 13px; color: #ddd; font-weight: 500; }
    .session-dir { font-size: 11px; color: #666; margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .no-sessions { padding: 20px 14px; color: #555; font-size: 12px; text-align: center; }

    /* Toggle button */
    #toggle-btn {
      position: fixed; left: 6px; top: 6px; z-index: 10;
      background: #252525; border: 1px solid #444; color: #aaa;
      cursor: pointer; font-size: 14px; padding: 4px 8px; border-radius: 4px;
      display: none;
    }
    #toggle-btn:hover { background: #333; color: #fff; }
    #sidebar.collapsed ~ #toggle-btn { display: block; }

    /* Terminal area — strictly fills remaining space, no overflow */
    #terminal-wrap {
      flex: 1; min-width: 0;
      display: flex; flex-direction: column;
      overflow: hidden;
    }
    #terminal {
      flex: 1;
      overflow: hidden;
      height: 0; /* Important: allows flex to control height */
      filter: brightness(0.8); /* 统一压暗所有文字（含 Claude 的真彩色白），减轻刺眼 */
    }

    /* Status bar — fixed height at bottom */
    #status-bar {
      height: 26px; line-height: 26px;
      background: #1e1e1e; color: #888;
      font-size: 12px; padding: 0 12px;
      display: flex; justify-content: space-between; align-items: center;
      border-top: 1px solid #333; flex-shrink: 0;
    }
    .connected { color: #aaa; }
    .disconnected { color: #666; }
    .connecting { color: #888; }
    #status-brand { color: #555; text-decoration: none; transition: color 0.2s; }
    #status-brand:hover { color: #888; }

    /* Touch pad position */
    #touch-pad {
      position: fixed; right: 16px; bottom: 36px; z-index: 20;
      opacity: 0.4; transition: opacity 0.2s;
      pointer-events: none;
    }
    #touch-pad:hover, #touch-pad:active { opacity: 0.85; }
    #dpad {
      display: grid;
      grid-template-columns: 44px 44px 44px;
      grid-template-rows: 44px 44px;
      gap: 3px;
    }
    .dpad-btn {
      width: 44px; height: 44px;
      background: #333; border: 1px solid #555; border-radius: 6px;
      color: #ccc; font-size: 18px;
      cursor: pointer; display: flex; align-items: center; justify-content: center;
      user-select: none; -webkit-user-select: none;
      touch-action: manipulation;
      pointer-events: auto;
    }
    .dpad-btn:active { background: #555; }
    .dpad-btn.bksp  { grid-column: 1; grid-row: 1; font-size: 15px; }
    .dpad-btn.up    { grid-column: 2; grid-row: 1; }
    .dpad-btn.enter { grid-column: 3; grid-row: 1; font-size: 15px; }
    .dpad-btn.left  { grid-column: 1; grid-row: 2; }
    .dpad-btn.down  { grid-column: 2; grid-row: 2; }
    .dpad-btn.right { grid-column: 3; grid-row: 2; }

    ::-webkit-scrollbar { width: 8px; }
    ::-webkit-scrollbar-track { background: #1e1e1e; }
    ::-webkit-scrollbar-thumb { background: #424242; border-radius: 4px; }
    ::-webkit-scrollbar-thumb:hover { background: #555; }

    /* Mobile styles */
    @media screen and (max-width: 600px) {
      #sidebar { width: 180px; min-width: 180px; }
      #sidebar.collapsed { margin-left: -180px; }
      #sidebar-header { padding: 10px 12px; font-size: 12px; }
      .session-item { padding: 8px 12px; }
      .session-label { font-size: 12px; }
      .session-dir { font-size: 10px; }
      #status-bar { height: 24px; min-height: 24px; line-height: 24px; font-size: 11px; padding: 0 8px; }
      #dpad { grid-template-columns: 40px 40px 40px; grid-template-rows: 40px 40px; gap: 2px; }
      .dpad-btn { width: 40px; height: 40px; font-size: 16px; }
      .dpad-btn.bksp, .dpad-btn.enter { font-size: 13px; }
      #touch-pad { right: 8px; bottom: 32px; }
    }

    @media screen and (max-width: 400px) {
      #sidebar { width: 160px; min-width: 160px; }
      #sidebar.collapsed { margin-left: -160px; }
      #toggle-btn { left: 4px; top: 4px; font-size: 12px; padding: 3px 6px; }
    }

    /* Small screen - auto collapse sidebar */
    @media screen and (max-width: 480px) {
      #sidebar:not(.user-toggled) { margin-left: -220px; }
      #sidebar:not(.user-toggled).collapsed ~ #toggle-btn { display: block; }
    }
  </style>
  <link rel="stylesheet" href="/xterm/xterm.css"/>
</head>
<body>
  <div id="app">
    <div id="sidebar">
      <div id="sidebar-header">
        <span>CC Pad Remote</span>
        <button onclick="toggleSidebar()" title="{{CCPad.Localization.Loc.T("web_collapse")}}">✕</button>
      </div>
      <div id="session-list"></div>
    </div>
    <button id="toggle-btn" onclick="toggleSidebar()" title="{{CCPad.Localization.Loc.T("web_expand")}}">☰</button>
    <div id="terminal-wrap">
      <div id="terminal"></div>
      <div id="status-bar">
        <span id="status">Connecting...</span>
        <span id="session-info"></span>
      </div>
    </div>
  </div>

  <!-- Touch controls -->
  <div id="touch-pad">
    <div id="dpad">
      <button class="dpad-btn bksp" onmousedown="event.preventDefault();sendKey('\x7f')" ontouchend="sendKey('\x7f')" ontouchstart="event.preventDefault()">⌫</button>
      <button class="dpad-btn up" onmousedown="event.preventDefault();sendKey('\x1b[A')" ontouchend="sendKey('\x1b[A')" ontouchstart="event.preventDefault()">▲</button>
      <button class="dpad-btn enter" onmousedown="event.preventDefault();sendKey('\r')" ontouchend="sendKey('\r')" ontouchstart="event.preventDefault()">⏎</button>
      <button class="dpad-btn left" onmousedown="event.preventDefault();sendKey('\x1b[D')" ontouchend="sendKey('\x1b[D')" ontouchstart="event.preventDefault()">◀</button>
      <button class="dpad-btn down" onmousedown="event.preventDefault();sendKey('\x1b[B')" ontouchend="sendKey('\x1b[B')" ontouchstart="event.preventDefault()">▼</button>
      <button class="dpad-btn right" onmousedown="event.preventDefault();sendKey('\x1b[C')" ontouchend="sendKey('\x1b[C')" ontouchstart="event.preventDefault()">▶</button>
    </div>
  </div>

  <script src="/xterm/xterm.js"></script>
  <script src="/xterm/xterm-addon-fit.js"></script>
  <script>
    const TOKEN = '{{token}}';
    const statusEl = document.getElementById('status');
    const sessionInfoEl = document.getElementById('session-info');
    const sessionListEl = document.getElementById('session-list');
    let ws, currentSessionId = null, sessions = [];
    let lockedCols = 0, lockedRows = 0;
    let heartbeatTimer = null, lastMsgTime = 0, reconnectDelay = 1000;
    let reconnectTimer = null, pongTimer = null;

    const term = new Terminal({
      fontFamily: "'Cascadia Code', 'Microsoft YaHei', 'Cascadia Mono', Consolas, monospace",
      fontSize: 16, lineHeight: 1.25,
      theme: { background: '#0c0c0c' },
      cursorBlink: true, allowProposedApi: true
    });
    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(document.getElementById('terminal'));
    fit.fit();

    function setStatus(text, cls) { statusEl.textContent = text; statusEl.className = cls; }

    const SIDEBAR_STATE_KEY = 'ccpad_sidebar_collapsed';

    function toggleSidebar() {
      const sidebar = document.getElementById('sidebar');
      sidebar.classList.toggle('collapsed');
      sidebar.classList.add('user-toggled');
      const isCollapsed = sidebar.classList.contains('collapsed');
      try { localStorage.setItem(SIDEBAR_STATE_KEY, isCollapsed ? '1' : '0'); } catch {}
      if (!lockedCols) setTimeout(() => fit.fit(), 250);
      else setTimeout(() => term.resize(lockedCols, lockedRows || term.rows), 250);
    }

    function restoreSidebarState() {
      const sidebar = document.getElementById('sidebar');
      try {
        const saved = localStorage.getItem(SIDEBAR_STATE_KEY);
        if (saved === '1') {
          sidebar.classList.add('collapsed');
          sidebar.classList.add('user-toggled');
        }
      } catch {}
    }

    restoreSidebarState();

    // Prevent double-tap zoom on mobile
    let lastTouchEnd = 0;
    document.addEventListener('touchend', (e) => {
      const now = Date.now();
      if (now - lastTouchEnd <= 300) {
        e.preventDefault();
      }
      lastTouchEnd = now;
    }, { passive: false });

    // Prevent pinch zoom
    document.addEventListener('touchmove', (e) => {
      if (e.touches.length > 1) {
        e.preventDefault();
      }
    }, { passive: false });

    async function refreshSessions() {
      try {
        const res = await fetch(`/api/sessions?token=${TOKEN}`);
        if (!res.ok) return;
        sessions = await res.json();
        renderSessionList();
      } catch {}
    }

    function renderSessionList() {
      if (sessions.length === 0) {
        sessionListEl.innerHTML = '<div class="no-sessions">{{CCPad.Localization.Loc.T("web_no_sessions")}}</div>';
        return;
      }
      sessionListEl.innerHTML = sessions.map(s => `
        <div class="session-item ${s.id === currentSessionId ? 'active' : ''}"
             onclick="attachSession('${s.id}')" title="${s.workingDir || ''}">
          <div class="session-label">${escHtml(s.label || s.command)}</div>
          <div class="session-dir">${escHtml(s.workingDir ? s.workingDir.split('\\\\').pop() : '')}</div>
        </div>
      `).join('');
    }

    function escHtml(s) {
      return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
    }

    function attachSession(id) {
      if (id === currentSessionId) return;
      currentSessionId = id;
      term.reset();
      lockedCols = 0;
      lockedRows = 0;
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'attach', sessionId: id }));
      }
      const s = sessions.find(x => x.id === id);
      sessionInfoEl.textContent = s ? (s.label || s.command) : '';
      renderSessionList();
      term.focus();
    }

    function clearTimers() {
      if (heartbeatTimer) { clearInterval(heartbeatTimer); heartbeatTimer = null; }
      if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
      if (pongTimer) { clearTimeout(pongTimer); pongTimer = null; }
    }

    function scheduleReconnect(delay) {
      clearTimers();
      setStatus('Disconnected — reconnecting...', 'disconnected');
      reconnectTimer = setTimeout(() => { reconnectTimer = null; connect(); }, delay);
    }

    function forceReconnect() {
      clearTimers();
      if (ws) { try { ws.onclose = null; ws.close(); } catch {} ws = null; }
      reconnectDelay = 1000;
      connect();
    }

    function connect() {
      clearTimers();
      const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
      ws = new WebSocket(`${proto}//${location.host}/ws?token=${TOKEN}`);
      setStatus('Connecting...', 'connecting');

      ws.onopen = () => {
        setStatus('Connected', 'connected');
        reconnectDelay = 1000;
        lastMsgTime = Date.now();

        // Start heartbeat: ping every 15s, check for stale connection every 20s
        heartbeatTimer = setInterval(() => {
          if (!ws || ws.readyState !== WebSocket.OPEN) return;
          // If no message received in 25s, connection is likely dead
          if (Date.now() - lastMsgTime > 25000) {
            forceReconnect();
            return;
          }
          ws.send(JSON.stringify({ type: 'ping' }));
        }, 15000);

        refreshSessions().then(() => {
          if (currentSessionId) {
            term.reset();
            ws.send(JSON.stringify({ type: 'attach', sessionId: currentSessionId }));
          } else if (sessions.length > 0) {
            attachSession(sessions[0].id);
          }
        });
      };

      ws.onmessage = (e) => {
        lastMsgTime = Date.now();
        const msg = JSON.parse(e.data);
        if (msg.type === 'output') {
          const bin = atob(msg.data);
          const bytes = new Uint8Array(bin.length);
          for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
          term.write(bytes);
        } else if (msg.type === 'attached') {
          currentSessionId = msg.sessionId;
          renderSessionList();
          term.focus();
        } else if (msg.type === 'size') {
          lockedCols = msg.cols;
          lockedRows = msg.rows;
          term.resize(msg.cols, msg.rows);
          const s = sessions.find(x => x.id === currentSessionId);
          sessionInfoEl.textContent = (s ? (s.label || s.command) : '') + ` [${msg.cols}x${msg.rows}]`;
        } else if (msg.type === 'replay') {
          term.reset();
          const bin = atob(msg.data);
          const bytes = new Uint8Array(bin.length);
          for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
          term.write(bytes);
        } else if (msg.type === 'pong') {
          // Heartbeat response received, connection is alive
        } else if (msg.type === 'error') {
          term.write('\r\n\x1b[31m' + (msg.message || 'Error') + '\x1b[0m\r\n');
        }
      };

      ws.onclose = () => {
        clearTimers();
        reconnectDelay = Math.min(reconnectDelay * 1.5, 10000);
        scheduleReconnect(reconnectDelay);
      };
      ws.onerror = () => { try { ws.close(); } catch {} };
    }

    // Reconnect immediately when page becomes visible (mobile resume)
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState !== 'visible') return;
      if (!ws || ws.readyState !== WebSocket.OPEN) {
        forceReconnect();
      } else {
        // Connection appears open — verify with a ping
        lastMsgTime = Date.now();
        try { ws.send(JSON.stringify({ type: 'ping' })); } catch { forceReconnect(); return; }
        pongTimer = setTimeout(() => {
          // No pong received in 3s, connection is stale
          if (Date.now() - lastMsgTime > 2500) forceReconnect();
        }, 3000);
      }
    });

    function sendKey(seq) {
      if (ws && ws.readyState === WebSocket.OPEN)
        ws.send(JSON.stringify({ type: 'input', data: seq }));
    }

    term.onData(data => {
      if (ws && ws.readyState === WebSocket.OPEN)
        ws.send(JSON.stringify({ type: 'input', data }));
    });

    new ResizeObserver(() => {
      if (!lockedCols) {
        fit.fit();
      } else {
        // Keep locked cols and rows to match desktop terminal exactly
        term.resize(lockedCols, lockedRows || term.rows);
      }
    }).observe(document.getElementById('terminal'));
    term.focus();
    connect();

    setInterval(refreshSessions, 3000);
  </script>
</body>
</html>
""";
    }
}
