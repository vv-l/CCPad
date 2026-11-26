"""Real Claude CLI, two local HTTP fixtures; never contacts a model provider.

Run: python tests/claude_api_isolation.py <launch-environments.json from ClaudeApi tests>
"""
import concurrent.futures
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

environments = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
assert environments[0]["ANTHROPIC_AUTH_TOKEN"] == "fixture-A"
assert environments[1]["ANTHROPIC_API_KEY"] == "fixture-B"
root = Path(tempfile.mkdtemp(prefix="ccpad-cli-api-isolation-"))
config = root / "config"
work = root / "work"
config.mkdir()
(work / ".claude").mkdir(parents=True)
wrong = {"env": {"ANTHROPIC_AUTH_TOKEN": "fixture-wrong-global", "ANTHROPIC_API_KEY": "fixture-wrong-global",
                 "ANTHROPIC_BASE_URL": "http://127.0.0.1:9"}}
(config / "settings.json").write_text(json.dumps(wrong))
(work / ".claude" / "settings.local.json").write_text(json.dumps(wrong))
(config / ".claude.json").write_text(json.dumps({"hasCompletedOnboarding": True}))
before = (config / "settings.json").read_bytes()
seen = []


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length", "0")))
        seen.append((self.server.server_port, self.headers.get("Authorization"), self.headers.get("x-api-key")))
        body = json.dumps({"type": "error", "error": {"type": "authentication_error", "message": "offline fixture complete"}}).encode()
        self.send_response(401)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass


servers = [ThreadingHTTPServer(("127.0.0.1", 0), Handler) for _ in range(2)]
for server in servers:
    threading.Thread(target=server.serve_forever, daemon=True).start()
native = Path(os.environ["APPDATA"]) / "npm/node_modules/@anthropic-ai/claude-code/bin/claude.exe"


def run(index):
    env = os.environ.copy()
    env.update(environments[index])
    env.update({"CLAUDE_CONFIG_DIR": str(config), "ANTHROPIC_BASE_URL": f"http://127.0.0.1:{servers[index].server_port}",
                "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC": "1", "DISABLE_AUTOUPDATER": "1", "PATH": str(native.parent) + ";" + env["PATH"]})
    command = [str(native), "-p", "offline fixture", "--model", "claude-sonnet-4-5", "--max-turns", "1", "--no-session-persistence"]
    if index == 1:
        # Same private environment survives a fallback cmd and a manually typed claude.
        command = [os.environ.get("COMSPEC", "cmd.exe"), "/d", "/c", "claude", *command[1:]]
    return subprocess.run(command, env=env, cwd=work, capture_output=True, timeout=25,
                          creationflags=subprocess.CREATE_NO_WINDOW).returncode


try:
    with concurrent.futures.ThreadPoolExecutor(2) as pool:
        list(pool.map(run, [0, 1]))
    assert (servers[0].server_port, "Bearer fixture-A", None) in seen, seen
    assert (servers[1].server_port, None, "fixture-B") in seen, seen
    assert all(row in [(servers[0].server_port, "Bearer fixture-A", None), (servers[1].server_port, None, "fixture-B")] for row in seen), seen
    assert (config / "settings.json").read_bytes() == before
    print("PASS concurrent native Claude A and fallback-shell Claude B use their own keys and endpoints")
    print("PASS user/project settings cannot override selected keys; original settings unchanged")
finally:
    for server in servers:
        server.shutdown()
