# scripts/

Small Python helpers for the WinDbgMCP SSE endpoint exposed by `mcp-proxy`.
They use only the Python standard library.

Default MCP endpoint:

```text
http://<DEBUGGER_HOST>:8002/sse
```

Override with flags where supported, or with:

```bash
WINDBG_MCP_HOST=<DEBUGGER_HOST>
WINDBG_MCP_PORT=8002
```

## Read-Only MCP Check

This performs an MCP handshake and calls `get_system_state`. It should not attach KD or change target execution state.

```bash
python scripts/smoke_test.py
```

With `uv`:

```bash
UV_CACHE_DIR=/tmp/uv-cache UV_TOOL_DIR=/tmp/uv-tools uv run scripts/smoke_test.py
```

Call a single MCP tool directly:

```bash
python scripts/mcp_client.py get_system_state
python scripts/mcp_client.py kd_symbol_status
python scripts/mcp_client.py kd_find_process_by_name '{"name":"lsass.exe"}'
```

## KD Smoke Test

This is side-effecting. It can connect KD, break the target, run WinDbg inspection commands, continue, and disconnect.

It prints each MCP `tools/call` request before sending it and prints each response after it returns:

```bash
python scripts/smoke_test.py --kd --trace-json
```

Save exact JSON-RPC request/response records to JSONL:

```bash
python scripts/smoke_test.py --kd --trace-json --transcript /tmp/windbg-kd-smoke.jsonl
```

Default KD commands are `k` and `r`. Add raw WinDbg commands with repeatable `--command`; these are sent to `kd_execute`:

```bash
python scripts/smoke_test.py --kd --trace-json --command k --command r --command "lm"
```

Leave the target broken for manual follow-up:

```bash
python scripts/smoke_test.py --kd --trace-json --leave-broken --no-disconnect
```

Limit printed response size while keeping full JSONL transcript:

```bash
python scripts/smoke_test.py --kd --trace-json --response-chars 2000 --transcript /tmp/windbg-kd-smoke.jsonl
```

## Helper MCP Calls

Normal symbol-backed process workflow:

```bash
python scripts/mcp_client.py kd_symbol_status
python scripts/mcp_client.py kd_find_process_by_name '{"name":"lsass.exe"}'
python scripts/mcp_client.py kd_list_threads '{"process":"<EPROCESS>"}'
python scripts/mcp_client.py kd_stack_process_thread '{"process":"<EPROCESS>","thread":"<ETHREAD>","command":"kv"}'
```

Raw-offset fallback when `!process` is broken by incomplete symbols:

```bash
python scripts/mcp_client.py kd_find_process_by_name_raw '{"name":"lsass.exe","uniquePidOffset":"0x480","activeLinksOffset":"0x488","imageNameOffset":"0x5e8"}'
python scripts/mcp_client.py kd_list_threads_raw '{"process":"<EPROCESS>","threadListHeadOffset":"0x620","threadListEntryOffset":"<confirmed>","cidPidOffset":"<confirmed>","cidTidOffset":"<confirmed>"}'
```

Server-side transcript for one noisy WinDbg command:

```bash
python scripts/mcp_client.py kd_execute '{"command":"!process 0 0","saveTranscript":true}'
```

## KD Convenience Scripts

These can attach KD, break the target, resume it, or detach:

```bash
python scripts/connect.py --kernel
python scripts/connect.py --kernel --break-on-connect
python scripts/disconnect.py --all
```

## Windows MCP Supervisor

Run `mcp-proxy` and `WinDbgMCP.Server.exe` in a restart loop on the Windows
debugger host. The supervisor monitors the backend `WinDbgMCP.Server.exe`
process and restarts `mcp-proxy` if that backend exits while the proxy keeps
listening. Normal server configuration still comes from `appsettings.json`
beside `WinDbgMCP.Server.exe`, inherited `WINDBG_MCP_*` environment variables,
or server command-line arguments:

```cmd
scripts\windbg-mcp-supervisor.cmd
```

The default server binary path is:

```text
C:\DATA\tools\windbg-mcp\src\WinDbgMCP.Server\bin\Debug\net8.0-windows\win-x64\WinDbgMCP.Server.exe
```

Override it if needed:

```cmd
scripts\windbg-mcp-supervisor.cmd -ServerExe C:\path\to\WinDbgMCP.Server.exe
```

If you do not keep the KDNET key in `appsettings.json`, pass it to the
supervisor:

```cmd
scripts\windbg-mcp-supervisor.cmd -KdnetKey YOUR.KDNET.KEY
```

## Frida

These MCP scripts do not manage Frida in the current deployment. Use direct operator-host Frida commands instead:

```bash
frida-ps -H <TARGET_IP>:27042
frida -H <TARGET_IP>:27042 -n notepad.exe
frida -H <TARGET_IP>:27042 -p <PID>
```

## Files

- `mcp_client.py`: reusable MCP-over-SSE client and raw tool-call CLI.
- `connect.py`: KD convenience attach workflow.
- `disconnect.py`: clean KD teardown.
- `smoke_test.py`: read-only MCP check or side-effecting KD smoke test.
