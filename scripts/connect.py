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
import time

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


def kd_exec_status(system_state_text):
    for line in system_state_text.splitlines():
        if line.strip().lower().startswith("kd exec status:"):
            return line.split(":", 1)[1].strip()
    return None


def auto_continue_kernel(c):
    for attempt in range(2):
        err, txt = c.call_tool("kd_continue")
        print(txt)
        if not tool_ok(err, txt):
            return False

        # The event pump can update KD state shortly after kd_continue returns.
        # Give it a small settle window before deciding auto-resume succeeded.
        time.sleep(1.0)

        err, state_text = c.call_tool("get_system_state")
        if err:
            print(state_text)
            return False

        status = kd_exec_status(state_text)
        if status and status.lower() in ("go", "gohandled"):
            return True

        if attempt == 0 and status and status.lower() == "break":
            print("[*] target still reports Break after auto-continue; retrying kd_continue once")
            continue

        print("[!] target did not enter Go state after kd_continue")
        print(state_text)
        return False

    return False


def main() -> int:
    ap = argparse.ArgumentParser(description="Connect to windbg-mcp and optionally attach KD.")
    ap.add_argument("--host")
    ap.add_argument("--port", type=int)
    ap.add_argument("--kernel", action="store_true", help="call kd_connect")
    ap.add_argument("--connect-timeout", type=int, default=60, help="seconds to wait for kd_connect")
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
        err, txt = c.call_tool("kd_connect", timeout=args.connect_timeout)
        print(txt)
        if not tool_ok(err, txt):
            return 1

        if not args.break_on_connect and "target is at initial breakpoint" in txt.lower():
            if not auto_continue_kernel(c):
                return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
