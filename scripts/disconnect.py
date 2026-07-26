#!/usr/bin/env python3
"""
Cleanly detach KD from WinDbgMCP.

Examples:
  ./disconnect.py
  ./disconnect.py --host <DEBUGGER_HOST> --port 8002

kd_disconnect resumes the target before detaching. If KD is not connected, the
server returns a harmless tool error.
"""

import argparse
import sys

from mcp_client import McpSseClient


def tool_ok(err, txt):
    if err:
        return False
    failure_markers = (
        " completed with error",
        " failed",
        "timed out",
        "cannot ",
        "requires ",
        "not connected",
        "not attached",
        "unknown action",
    )
    lowered = txt.lower()
    return not any(marker in lowered for marker in failure_markers)


def main() -> int:
    ap = argparse.ArgumentParser(description="Detach KD from windbg-mcp.")
    ap.add_argument("--host")
    ap.add_argument("--port", type=int)
    args = ap.parse_args()

    c = McpSseClient(args.host, args.port)
    try:
        c.connect()
    except Exception:
        print(f"[!] failed to connect to {c.base} - is mcp-proxy running?")
        return 1

    err, txt = c.call_tool("kd_disconnect")
    print(txt)
    return 0 if tool_ok(err, txt) else 1


if __name__ == "__main__":
    sys.exit(main())
