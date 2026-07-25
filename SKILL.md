---
name: windbg-mcp
description: Use the WinDbgMCP server for Windows KDNET kernel debugging through a Windows debugger host. In this deployment, Frida is accessed directly from the operator/LLM host to the target/debuggee, not through MCP.
---

# windbg-mcp

Use this skill when working with the WinDbgMCP MCP server exposed by `mcp-proxy`.

## Topology

```text
MCP / KDNET:
  Codex or Claude -> Windows debugger host -> mcp-proxy -> WinDbgMCP.Server -> KDNET target

Frida:
  operator/LLM host -> frida-server on target/debuggee
```

Roles:

- **Operator/LLM host**: the machine running Codex/Claude. It can directly reach `frida-server`.
- **Windows debugger host**: runs `mcp-proxy`, `WinDbgMCP.Server.exe`, and DbgEng for KDNET.
- **Target/debuggee**: KDNET target. It may also run `frida-server`.

Frida is not an MCP endpoint in this deployment. Do not call `umd_frida_*` tools unless the server explicitly reports server-side user-mode tools enabled.

Do not use VMware, `vmrun`, `vm_*`, `guest_*`, snapshots, screenshots, or guest file transfer unless the server explicitly reports `Vm.VmwareEnabled=true`.

## MCP Endpoints

Claude:

```bash
claude mcp add --scope project --transport sse windbg-mcp http://<DEBUGGER_HOST>:8002/sse
```

Codex:

```bash
codex mcp add windbg-mcp --url http://<DEBUGGER_HOST>:8002/mcp
```

Debugger host endpoint shape:

```text
<DEBUGGER_HOST>:8002
```

## KD MCP Workflow

Use these MCP tools for normal KD work:

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

Minimal attach/inspect/resume:

```text
get_system_state
kd_connect
kd_break
kd_execute command="lm"
kd_execute command="k"
kd_execute command="r"
kd_continue
kd_disconnect
```

To inspect a user process from KD, break the whole target first, then switch process/thread context:

```text
kd_break
kd_symbol_status
kd_find_process_by_name name="lsass.exe"
kd_list_threads process="<EPROCESS>"
kd_stack_process_thread process="<EPROCESS>" thread="<ETHREAD>" command="kv"
kd_continue
```

`kd_execute` commands include `lm`, `r`, `k`, `dq`, `dd`, `db`, `u`, `x`, `.reload`, `!process 0 0`, and `!analyze -v`. Execution-control commands such as `g`, `p`, and `t` are blocked; use `kd_continue` or `kd_step`.

To save full server-side output for a noisy command:

```text
kd_execute command="!process 0 0" saveTranscript=true
```

The returned path is under `KernelDebug.TranscriptDirectory` on the Windows debugger host.

## Symbol Failure Workflow

If `kd_find_process_by_name`, `kd_list_threads`, or `!process` reports bad or incomplete symbols:

```text
kd_symbol_status
kd_execute command=".symfix"
kd_execute command=".reload /f nt"
kd_execute command="lm vm nt"
```

If `nt` remains export-only, the KD transport can still be healthy while `!process` is unusable. Use raw helpers only with offsets confirmed for the exact target build.

Offsets observed in the repo `issue.md` for one Windows Server 2022 target:

```text
EPROCESS + 0x480 = UniqueProcessId
EPROCESS + 0x488 = ActiveProcessLinks
EPROCESS + 0x5e8 = ImageFileName
EPROCESS + 0x620 = ThreadListHead
```

These are examples from that target, not universal constants. Confirm ETHREAD offsets before calling `kd_list_threads_raw`.

Raw fallback:

```text
kd_find_process_by_name_raw name="lsass.exe" uniquePidOffset="0x480" activeLinksOffset="0x488" imageNameOffset="0x5e8"
kd_list_threads_raw process="<EPROCESS>" threadListHeadOffset="0x620" threadListEntryOffset="<ETHREAD.ThreadListEntry>" cidPidOffset="<ETHREAD.ClientId.UniqueProcess>" cidTidOffset="<ETHREAD.ClientId.UniqueThread>"
kd_stack_process_thread process="<EPROCESS>" thread="<ETHREAD>" command="kv"
```

If `.process /r /p` reports `PEB address is NULL`, or `.thread` cannot retrieve thread context, report that the kernel stack may still be valid but user-mode frames are unavailable in that session.

## Direct Frida Workflow

Use local/operator-host Frida commands, not MCP:

```bash
frida-ps -H <TARGET_IP>:27042
frida -H <TARGET_IP>:27042 -n notepad.exe
frida -H <TARGET_IP>:27042 -p <PID>
```

For Frida 17 JavaScript, prefer:

```js
const addr = Process.getModuleByName('ntdll.dll').getExportByName('NtClose');
```

or:

```js
const addr = Module.getGlobalExportByName('NtClose');
```

Troubleshoot Frida from the operator/LLM host:

- Check connectivity to `<TARGET_IP>:27042`.
- Check the target firewall.
- Check that `frida-server` is running on the target/debuggee.
- Check that local Frida tools and target `frida-server` versions are compatible.

## Smoke And Transcripts

Read-only MCP check:

```bash
UV_CACHE_DIR=/tmp/uv-cache UV_TOOL_DIR=/tmp/uv-tools uv run scripts/smoke_test.py
```

Side-effecting KD smoke test with exact JSON-RPC records:

```bash
UV_CACHE_DIR=/tmp/uv-cache UV_TOOL_DIR=/tmp/uv-tools \
  uv run scripts/smoke_test.py --kd --trace-json --transcript /tmp/windbg-kd-smoke.jsonl
```

The KD smoke test can attach, break, run commands, continue, and disconnect. Use `--leave-broken --no-disconnect` only when intentionally preserving debugger state.

## MCP Troubleshooting

- `Connection refused` on MCP URL: `mcp-proxy` is not running on the debugger host, is not bound to `0.0.0.0`, or a firewall is blocking it.
- `MCP error -32000: Connection closed` from `mcp-proxy`: the child `WinDbgMCP.Server.exe` exited. Run the EXE directly on the debugger host and check for missing `appsettings.json`, missing runtime, bad config, or missing debugger components.
- `kd_connect` fails: verify KDNET UDP port/key, target `bcdedit /dbgsettings`, debugger-host firewall, and that no other debugger is attached.
- Tool error says VMware integration is disabled: use only KD tools and direct operator-host Frida.
