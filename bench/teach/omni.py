"""Minimal MCP stdio client for OmniWin, used by the teaching runs (reviewer, 2026-11-17).
One OmniWin.Server process per run, so an episode (omniwin_do ... task_done) stays in one process."""
import json, re, subprocess, time

class Omni:
    def __init__(self, exe, data_dir):
        self.p = subprocess.Popen([exe, "--data-dir", data_dir], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
        self.i = 0
        self._rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "omniwin-teacher", "version": "1"}})
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def _send(self, m):
        self.p.stdin.write(json.dumps(m) + "\n"); self.p.stdin.flush()

    def _rpc(self, method, params):
        self.i += 1
        self._send({"jsonrpc": "2.0", "id": self.i, "method": method, "params": params})
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError("server closed")
            m = json.loads(line)
            if m.get("id") == self.i:
                return m

    def call(self, name, args=None):
        m = self._rpc("tools/call", {"name": name, "arguments": args or {}})
        if "error" in m:
            return "status=error rpc " + json.dumps(m["error"])[:300]
        return "".join(c.get("text", "") for c in m["result"]["content"] if c.get("type") == "text")

    def close(self):
        try:
            self.p.stdin.close(); self.p.wait(timeout=30)
        except Exception:
            self.p.kill()

def status(text):
    m = re.search(r"status=(\w+)", text.split("\n", 1)[0])
    return m.group(1) if m else "?"
