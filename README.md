# WinDbgMCP

## Fork Changes

This fork targets a no-VMware KDNET deployment: Codex/Claude connect to `mcp-proxy` on a Windows debugger host, and Frida is used directly from the operator host.
It adds externally managed targets, VMware-disabled mode, safer tool registration, KD process/thread helpers, transcript support, helper scripts, and cleaner install docs.

WinDbgMCP is a Windows-hosted MCP server for WinDbg/DbgEng KDNET debugging.

Current deployment topology:

```text
MCP / KDNET:
  Codex or Claude -> Windows debugger host -> mcp-proxy -> WinDbgMCP.Server -> KDNET target

Frida:
  Codex or Claude/operator host -> frida-server on target/debuggee
```

The Windows debugger host does not need Frida tools for this setup. Frida is not an MCP endpoint here; connect to `frida-server` directly from the operator/LLM host.

The current no-VMware deployment also does not use `vmrun`, snapshots, guest file transfer, screenshots, or VMware guest operations.

## Run On The Windows Debugger Host

See [INSTALL.md](INSTALL.md) for debugger-host and debuggee KDNET configuration.

Build or publish the server on the Windows debugger host, then run it behind `mcp-proxy`.

Debug build example:

```powershell
cd C:\tmp\windbg\windbg-mcp
dotnet build src\WinDbgMCP.Server\WinDbgMCP.Server.csproj
mcp-proxy --host 0.0.0.0 --port 8002 -- C:\tmp\windbg\windbg-mcp\src\WinDbgMCP.Server\bin\Debug\net8.0-windows\win-x64\WinDbgMCP.Server.exe
```

Published/single-directory example:

```powershell
mcp-proxy --host 0.0.0.0 --port 8002 -- C:\tmp\windbg\win-x64\WinDbgMCP.Server.exe
```

For file-based configuration, place `appsettings.json` next to `WinDbgMCP.Server.exe`. A single-file EXE can also run without that sidecar when configured with environment variables. If `mcp-proxy` reports `MCP error -32000: Connection closed`, run the EXE directly in PowerShell first; that usually exposes missing configuration, missing .NET runtime, bad config, or missing Windows debugging components.

### Standalone EXE

Publish a self-contained single-file Windows executable:

```powershell
dotnet publish src\WinDbgMCP.Server\WinDbgMCP.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish\win-x64
```

`WinDbgMCP.Server.exe` can run without an adjacent `appsettings.json` when configured with environment variables:

```powershell
$env:WINDBG_MCP_VMWARE_ENABLED="false"
$env:WINDBG_MCP_TARGET_HOST="<TARGET_IP>"
$env:WINDBG_MCP_KDNET_PORT="50000"
$env:WINDBG_MCP_KDNET_KEY="your.kdnet.key.here"
mcp-proxy --host 0.0.0.0 --port 8002 -- .\publish\win-x64\WinDbgMCP.Server.exe
```

Windows debugging components/DbgEng still need to be installed on the debugger host.

## Add The MCP Server

Claude uses the SSE endpoint:

```bash
claude mcp add --scope project --transport sse windbg-mcp http://<DEBUGGER_HOST>:8002/sse
```

Codex uses the streamable HTTP endpoint:

```bash
codex mcp add windbg-mcp --url http://<DEBUGGER_HOST>:8002/mcp
```

Replace `<DEBUGGER_HOST>` with the Windows debugger host IP or DNS name.

## Server Configuration

The server optionally reads `appsettings.json` from the server executable directory. Environment variables and command-line configuration can be used instead for standalone EXE deployments.

Recommended no-VMware/no-server-side-Frida shape:

```json
{
  "Target": {
    "Host": "<TARGET_IP>"
  },
  "UserModeDebug": {
    "ServerSideToolsEnabled": false
  },
  "Vm": {
    "VmwareEnabled": false,
    "GuestIpAddress": "<TARGET_IP>"
  },
  "KernelDebug": {
    "Transport": "kdnet",
    "Kdnet": {
      "Port": 50000,
      "Key": "your.kdnet.key.here"
    },
    "SymbolPath": "srv*C:\\Symbols*https://msdl.microsoft.com/download/symbols",
    "TranscriptDirectory": "C:\\tmp\\windbg-mcp\\transcripts"
  },
  "Guest": {
    "FridaPort": 27042
  }
}
```

`Target.Host` is the target/debuggee address. It is useful for status output and direct Frida documentation, but it is not an MCP endpoint. `Vm.GuestIpAddress` is kept as a legacy fallback for older configs.

## Expected MCP Tool Surface

With `Vm.VmwareEnabled=false` and `UserModeDebug.ServerSideToolsEnabled=false`, the useful MCP workflow is:

```text
get_system_state
kd_connect
kd_disconnect
kd_break
kd_continue
kd_step
kd_execute
kd_symbol_status
kd_find_process_by_name
kd_find_process_by_name_raw
kd_list_threads
kd_list_threads_raw
kd_switch_process
kd_switch_thread
kd_stack
kd_stack_process_thread
kd_wait_for_event
```

Do not use MCP for `vm_*`, `guest_*`, `umd_frida_*`, `umd_dbgsrv_*`, `umd_ttd`, snapshots, screenshots, or VMware file transfer in this deployment.

## KD Workflow

Minimal attach and inspection flow:

```text
get_system_state
kd_connect
kd_break
kd_execute command="lm"
kd_execute command="k"
kd_execute command="r"
kd_continue
kd_wait_for_event
kd_disconnect
```

To inspect a user process such as LSASS from KD, break the whole target first, then switch debugger context:

```text
kd_break
kd_symbol_status
kd_find_process_by_name name="lsass.exe"
kd_list_threads process="<EPROCESS>"
kd_stack_process_thread process="<EPROCESS>" thread="<ETHREAD>" command="kv"
kd_continue
```

`kd_execute` accepts normal inspection commands such as `lm`, `r`, `k`, `dq`, `dd`, `db`, `u`, `x`, `.reload`, `!process 0 0`, and `!analyze -v`. Execution-control commands such as `g`, `p`, and `t` are blocked; use `kd_continue` and `kd_step`.

## Symbol Problems And Raw Fallback

If `kd_find_process_by_name`, `kd_list_threads`, or `!process` reports incomplete symbols:

```text
kd_symbol_status
kd_execute command=".symfix"
kd_execute command=".reload /f nt"
kd_execute command="lm vm nt"
```

If `nt` still shows export-only symbols, `!process` may remain unusable even though KD itself works. Use the raw helpers only with offsets confirmed for the exact target build.

Offsets observed in `issue.md` for one Windows Server 2022 build were:

```text
EPROCESS + 0x480 = UniqueProcessId
EPROCESS + 0x488 = ActiveProcessLinks
EPROCESS + 0x5e8 = ImageFileName
EPROCESS + 0x620 = ThreadListHead
```

Those offsets are not universal. Confirm ETHREAD offsets separately before using `kd_list_threads_raw`.

Raw fallback example:

```text
kd_find_process_by_name_raw name="lsass.exe" uniquePidOffset="0x480" activeLinksOffset="0x488" imageNameOffset="0x5e8"
kd_list_threads_raw process="<EPROCESS>" threadListHeadOffset="0x620" threadListEntryOffset="<ETHREAD.ThreadListEntry>" cidPidOffset="<ETHREAD.ClientId.UniqueProcess>" cidTidOffset="<ETHREAD.ClientId.UniqueThread>"
kd_stack_process_thread process="<EPROCESS>" thread="<ETHREAD>" command="kv"
```

If `.process /r /p` says `PEB address is NULL` or `.thread` says it cannot retrieve thread context, a kernel stack can still be valid while user-mode frames are unavailable in that KD session.

## Transcripts

For exact WinDbg command output saved by the server:

```bash
python scripts/mcp_client.py kd_execute '{"command":"!process 0 0","saveTranscript":true}'
```

The returned path points under `KernelDebug.TranscriptDirectory`.

For exact MCP JSON-RPC request/response records from the client smoke test:

```bash
UV_CACHE_DIR=/tmp/uv-cache UV_TOOL_DIR=/tmp/uv-tools \
  uv run scripts/smoke_test.py --kd --trace-json --transcript /tmp/windbg-kd-smoke.jsonl
```

## Direct Frida

Run Frida from the operator/LLM host directly to the target/debuggee:

```bash
frida-ps -H <TARGET_IP>:27042
frida -H <TARGET_IP>:27042 -n notepad.exe
frida -H <TARGET_IP>:27042 -p <PID>
```

If Frida times out, check connectivity from the operator/LLM host to `<TARGET_IP>:27042`, target firewall rules, that `frida-server` is running on the target, and that local Frida tools match the target `frida-server` version.

For Frida 17 JavaScript, prefer:

```js
const addr = Process.getModuleByName('ntdll.dll').getExportByName('NtClose');
```

or:

```js
const addr = Module.getGlobalExportByName('NtClose');
```

## Helper Scripts

The scripts in `scripts/` are MCP/SSE helpers for the debugger host. They do not manage direct Frida.

Read-only MCP check:

```bash
UV_CACHE_DIR=/tmp/uv-cache UV_TOOL_DIR=/tmp/uv-tools uv run scripts/smoke_test.py
```

Side-effecting KD smoke test:

```bash
UV_CACHE_DIR=/tmp/uv-cache UV_TOOL_DIR=/tmp/uv-tools \
  uv run scripts/smoke_test.py --kd --trace-json --command k --command r
```

Leave the target broken for manual follow-up:

```bash
UV_CACHE_DIR=/tmp/uv-cache UV_TOOL_DIR=/tmp/uv-tools \
  uv run scripts/smoke_test.py --kd --trace-json --leave-broken --no-disconnect
```

Raw tool calls:

```bash
python scripts/mcp_client.py get_system_state
python scripts/mcp_client.py kd_symbol_status
python scripts/mcp_client.py kd_find_process_by_name '{"name":"lsass.exe"}'
python scripts/mcp_client.py kd_stack_process_thread '{"process":"<EPROCESS>","thread":"<ETHREAD>","command":"kv"}'
```

## Development

Build:

```bash
dotnet build src/WinDbgMCP.Server/WinDbgMCP.Server.csproj
```

Test:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test src/WinDbgMCP.Tests/WinDbgMCP.Tests.csproj
```

`DOTNET_ROLL_FORWARD=Major` is only needed on hosts that do not have the .NET 8 runtime installed.
