#!/usr/bin/env python3
"""
Connect to the WinDbgMCP endpoint and optionally attach KD.

Examples:
  ./connect.py
  ./connect.py --kernel
  ./connect.py --kernel --break-on-connect
  ./connect.py --host <DEBUGGER_HOST> --port 8002

Frida is direct from the operator host to the target/debuggee in the current
deployment; this script intentionally does not call umd_frida tools.
"""

import argparse
import sys

from mcp_client import McpSseClient


def main() -> int:
    ap = argparse.ArgumentParser(description="Connect to windbg-mcp and optionally attach KD.")
    ap.add_argument("--host")
    ap.add_argument("--port", type=int)
    ap.add_argument("--kernel", action="store_true", help="call kd_connect")
    ap.add_argument(
        "--break-on-connect",
        action="store_true",
        help="leave target halted after kd_connect; otherwise resume it",
    )
    args = ap.parse_args()

    c = McpSseClient(args.host, args.port)
    try:
        info = c.connect()
    except Exception:
        print(f"[!] failed to connect to {c.base} - is mcp-proxy running?")
        return 1

    print(f"[+] connected to {info.get('name', '?')} {info.get('version', '')}")

    err, txt = c.call_tool("get_system_state")
    print(txt)
    if err:
        return 1

    if args.kernel:
        err, txt = c.call_tool("kd_connect", timeout=45)
        print(txt)
        if err:
            return 1

        if not args.break_on_connect:
            err, txt = c.call_tool("kd_continue")
            print(txt)
            if err:
                return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
