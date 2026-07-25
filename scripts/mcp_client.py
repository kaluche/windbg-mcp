#!/usr/bin/env python3
"""
Minimal MCP-over-SSE client for the windbg-mcp server (exposed via mcp-proxy).

No third-party dependencies — uses only the Python stdlib. The server speaks the
SSE transport: open GET /sse to receive an `endpoint` event giving a POST URL, then
POST JSON-RPC requests there; responses arrive back on the SSE stream.

Used by connect.py and smoke_test.py. Can also be run directly to call one tool:

    ./mcp_client.py get_system_state
    ./mcp_client.py umd_frida_attach '{"processName":"explorer.exe"}'
"""
import json
import os
import queue
import sys
import threading
import time
import urllib.request


class McpSseClient:
    def __init__(self, host=None, port=None, path="/sse", client_name="windbg-mcp-script"):
        self.host = host or os.environ.get("WINDBG_MCP_HOST", "127.0.0.1")
        self.port = int(port or os.environ.get("WINDBG_MCP_PORT", "8002"))
        self.base = f"http://{self.host}:{self.port}"
        self.sse_url = self.base + path
        self.client_name = client_name
        self._responses = queue.Queue()
        self._endpoint = queue.Queue()
        self._next_id = 1
        self._msg_url = None

    # -- SSE plumbing -------------------------------------------------------
    def _sse_reader(self):
        req = urllib.request.Request(self.sse_url, headers={"Accept": "text/event-stream"})
        resp = urllib.request.urlopen(req, timeout=120)
        event = None
        for raw in resp:
            line = raw.decode("utf-8", "replace").rstrip("\n")
            if line.startswith("event:"):
                event = line[6:].strip()
            elif line.startswith("data:"):
                data = line[5:].strip()
                if event == "endpoint":
                    self._endpoint.put(data)
                elif event == "message":
                    try:
                        self._responses.put(json.loads(data))
                    except Exception:
                        pass
            elif line == "":
                event = None

    def _rid(self):
        i = self._next_id
        self._next_id += 1
        return i

    def _send(self, obj):
        data = json.dumps(obj).encode()
        req = urllib.request.Request(
            self._msg_url, data=data,
            headers={"Content-Type": "application/json"}, method="POST")
        urllib.request.urlopen(req, timeout=15).read()

    def _wait(self, rid, timeout=60):
        end = time.time() + timeout
        while time.time() < end:
            try:
                m = self._responses.get(timeout=max(0.1, end - time.time()))
            except queue.Empty:
                break
            if m.get("id") == rid:
                return m
        return None

    # -- public API ---------------------------------------------------------
    def connect(self, timeout=10):
        """Open the SSE channel and perform the MCP initialize handshake.
        Returns the server's serverInfo dict (or {} on failure)."""
        threading.Thread(target=self._sse_reader, daemon=True).start()
        self._msg_url = self.base + self._endpoint.get(timeout=timeout)
        rid = self._rid()
        self._send({"jsonrpc": "2.0", "id": rid, "method": "initialize",
                    "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                               "clientInfo": {"name": self.client_name, "version": "1.0"}}})
        init = self._wait(rid, timeout)
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
        return (init or {}).get("result", {}).get("serverInfo", {})

    def call_tool(self, name, arguments=None, timeout=60, on_send=None, on_receive=None):
        """Call an MCP tool. Returns (is_error: bool, text: str)."""
        rid = self._rid()
        request = {"jsonrpc": "2.0", "id": rid, "method": "tools/call",
                   "params": {"name": name, "arguments": arguments or {}}}
        if on_send is not None:
            on_send(request)
        self._send(request)
        r = self._wait(rid, timeout)
        if on_receive is not None:
            on_receive(r)
        if r is None:
            return True, f"<no response within {timeout}s>"
        if "error" in r:
            return True, json.dumps(r["error"])
        res = r.get("result", {})
        text = "\n".join(c.get("text", "") for c in res.get("content", []) if c.get("type") == "text")
        return bool(res.get("isError")), text


def _main(argv):
    if not argv:
        print(__doc__)
        return 2
    tool = argv[0]
    args = json.loads(argv[1]) if len(argv) > 1 else {}
    c = McpSseClient()
    info = c.connect()
    print(f"# connected to {info.get('name', '?')} {info.get('version', '')}", file=sys.stderr)
    err, text = c.call_tool(tool, args)
    print(text)
    return 1 if err else 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
