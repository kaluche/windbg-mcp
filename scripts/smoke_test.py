#!/usr/bin/env python3
"""
WinDbgMCP smoke test.

Default mode is read-only:
  ./smoke_test.py

KD mode is side-effecting:
  ./smoke_test.py --kd

KD mode connects to the kernel debugger if needed, breaks the target if it is
running, runs inspection commands, then continues and disconnects by default.
Use --leave-broken or --no-disconnect when you intentionally want to keep state.

Frida is direct from the operator host to the target/debuggee in the current
deployment; this script intentionally does not call umd_frida tools.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from mcp_client import McpSseClient


@dataclass
class ToolResult:
    name: str
    args: dict[str, Any]
    is_error: bool
    text: str


class SmokeRunner:
    def __init__(
        self,
        client: McpSseClient,
        *,
        trace_json: bool,
        response_chars: int,
        transcript: Path | None,
    ) -> None:
        self.client = client
        self.trace_json = trace_json
        self.response_chars = response_chars
        self.transcript = transcript
        self.ok = True
        self.results: list[ToolResult] = []
        if self.transcript:
            self.transcript.parent.mkdir(parents=True, exist_ok=True)
            self.transcript.write_text("", encoding="utf-8")

    def check(self, name: str, condition: bool, detail: str = "") -> None:
        self.ok = self.ok and bool(condition)
        mark = "PASS" if condition else "FAIL"
        print(f"[{mark}] {name}")
        if detail:
            print(_indent(detail.rstrip()))

    def call(self, name: str, args: dict[str, Any] | None = None, *, timeout: int = 60) -> ToolResult:
        print(f"\n>>> {name}")

        def on_send(request: dict[str, Any]) -> None:
            if self.trace_json:
                print(json.dumps(request, indent=2, sort_keys=True))
            else:
                print(f"args={json.dumps(args or {}, sort_keys=True)}")
            self._write_transcript({"direction": "send", "tool": name, "request": request})

        def on_receive(response: dict[str, Any] | None) -> None:
            self._write_transcript({"direction": "receive_raw", "tool": name, "response": response})

        is_error, text = self.client.call_tool(
            name,
            args or {},
            timeout=timeout,
            on_send=on_send,
            on_receive=on_receive,
        )
        result = ToolResult(name, args or {}, is_error, text)
        self.results.append(result)
        self._write_transcript({
            "direction": "receive_text",
            "tool": name,
            "is_error": is_error,
            "text": text,
        })

        status = "ERROR" if is_error else "OK"
        print(f"<<< {name} {status}")
        print(_indent(_trim(text, self.response_chars).rstrip()))
        return result

    def _write_transcript(self, record: dict[str, Any]) -> None:
        if not self.transcript:
            return
        record = {
            "ts": datetime.now(timezone.utc).isoformat(),
            **record,
        }
        with self.transcript.open("a", encoding="utf-8") as f:
            f.write(json.dumps(record, sort_keys=True) + "\n")


def _indent(text: str, prefix: str = "    ") -> str:
    if not text:
        return prefix + "<empty>"
    return "\n".join(prefix + line for line in text.splitlines())


def _trim(text: str, limit: int) -> str:
    if limit <= 0 or len(text) <= limit:
        return text
    return text[:limit] + f"\n... <truncated {len(text) - limit} chars>"


def _execution_status(state_text: str) -> str | None:
    patterns = [
        r"KD Exec Status:\s*([A-Za-z0-9_]+)",
        r"Execution Status:\s*([A-Za-z0-9_]+)",
    ]
    for pattern in patterns:
        match = re.search(pattern, state_text)
        if match:
            return match.group(1)
    return None


def _kd_connected(state_text: str) -> bool | None:
    match = re.search(r"KD Connected:\s*(True|False)", state_text)
    if not match:
        return None
    return match.group(1) == "True"


def _tool_ok(result: ToolResult) -> bool:
    if result.is_error:
        return False

    text = result.text.lower()
    failure_markers = [
        " completed with error",
        " failed",
        "timed out",
        "cannot ",
        "requires ",
        "not connected",
        "not attached",
        "unknown action",
    ]
    return not any(marker in text for marker in failure_markers)


def run_read_only(runner: SmokeRunner) -> None:
    state = runner.call("get_system_state", timeout=20)
    runner.check("get_system_state", _tool_ok(state) and "SYSTEM STATE" in state.text)


def run_kd(runner: SmokeRunner, args: argparse.Namespace) -> None:
    initial = runner.call("get_system_state", timeout=20)
    runner.check("initial state", _tool_ok(initial))

    connected = _kd_connected(initial.text)
    status = _execution_status(initial.text)

    if connected is not True:
        connect = runner.call("kd_connect", timeout=args.connect_timeout)
        runner.check("kd_connect", _tool_ok(connect))
        after_connect = runner.call("get_system_state", timeout=20)
        status = _execution_status(after_connect.text)
    else:
        runner.check("kd already connected", True, f"execution status: {status or 'unknown'}")

    if status != "Break":
        brk = runner.call("kd_break", timeout=args.break_timeout)
        runner.check("kd_break", _tool_ok(brk))
    else:
        runner.check("target already broken", True)

    broken = runner.call("get_system_state", timeout=20)
    status = _execution_status(broken.text)
    runner.check("target is broken", status == "Break", f"execution status: {status or 'unknown'}")

    for command in args.command:
        result = runner.call(
            "kd_execute",
            {"command": command, "timeoutSeconds": args.command_timeout},
            timeout=args.command_timeout + 10,
        )
        runner.check(f"kd_execute {command!r}", _tool_ok(result))

    if not args.leave_broken:
        cont = runner.call("kd_continue", timeout=args.control_timeout)
        runner.check("kd_continue", _tool_ok(cont))
    else:
        runner.check("leave target broken", True)

    if not args.no_disconnect:
        disc = runner.call("kd_disconnect", timeout=args.control_timeout)
        runner.check("kd_disconnect", _tool_ok(disc))


def main() -> int:
    ap = argparse.ArgumentParser(description="WinDbgMCP smoke test.")
    ap.add_argument("--host")
    ap.add_argument("--port", type=int)
    ap.add_argument("--kd", action="store_true", help="run side-effecting KD attach/break/inspect workflow")
    ap.add_argument(
        "--command",
        action="append",
        default=None,
        help="WinDbg command to run through kd_execute in --kd mode. Repeatable. Default: k, r",
    )
    ap.add_argument("--trace-json", action="store_true", help="print tools/call payloads as JSON")
    ap.add_argument("--transcript", type=Path, help="write exact sent/received tool records as JSONL")
    ap.add_argument("--response-chars", type=int, default=6000, help="max response chars to print per call; 0 means unlimited")
    ap.add_argument("--connect-timeout", type=int, default=45)
    ap.add_argument("--break-timeout", type=int, default=20)
    ap.add_argument("--command-timeout", type=int, default=30)
    ap.add_argument("--control-timeout", type=int, default=20)
    ap.add_argument("--leave-broken", action="store_true", help="do not continue after inspection")
    ap.add_argument("--no-disconnect", action="store_true", help="do not call kd_disconnect during cleanup")
    args = ap.parse_args()

    if args.command is None:
        args.command = ["k", "r"]

    client = McpSseClient(args.host, args.port)
    runner = SmokeRunner(
        client,
        trace_json=args.trace_json,
        response_chars=args.response_chars,
        transcript=args.transcript,
    )

    try:
        info = client.connect()
        runner.check("MCP handshake", True, f"{info.get('name', '?')} {info.get('version', '')}")
    except Exception as ex:
        runner.check("MCP handshake", False, str(ex))
        return 1

    if args.kd:
        run_kd(runner, args)
    else:
        run_read_only(runner)

    print("\nRESULT: " + ("PASS" if runner.ok else "FAIL"))
    return 0 if runner.ok else 1


if __name__ == "__main__":
    sys.exit(main())
