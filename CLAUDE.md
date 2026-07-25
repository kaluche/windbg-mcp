# WinDbgMCP Developer Notes

This fork is centered on KDNET through a Windows debugger host.

## Current Topology

```text
Codex/Claude -> mcp-proxy on debugger host -> WinDbgMCP.Server -> KDNET target
operator host -> frida-server on target/debuggee
```

VMware and server-side user-mode tooling are optional. In the no-VMware deployment, `vm_*`, `guest_*`, screenshots, snapshots, server-side Frida, dbgsrv, and TTD tools are not registered or are rejected up front.

## Build And Test

```bash
dotnet build src/WinDbgMCP.Server/WinDbgMCP.Server.csproj
DOTNET_ROLL_FORWARD=Major dotnet test src/WinDbgMCP.Tests/WinDbgMCP.Tests.csproj
dotnet publish src/WinDbgMCP.Server/WinDbgMCP.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/win-x64
```

Use `DOTNET_ROLL_FORWARD=Major` on hosts that do not have the .NET 8 runtime installed.

## Configuration

`src/WinDbgMCP.Server/appsettings.json` is intentionally ignored because it can contain KDNET keys and VM credentials. For standalone EXE use, prefer environment variables such as:

```text
WINDBG_MCP_VMWARE_ENABLED=false
WINDBG_MCP_TARGET_HOST=<TARGET_IP>
WINDBG_MCP_KDNET_PORT=50000
WINDBG_MCP_KDNET_KEY=<KDNET_KEY>
```

## Useful Tools

Normal no-VMware KD flow:

```text
get_system_state
kd_connect
kd_break
kd_execute
kd_continue
kd_disconnect
```

Additional KD helpers include `kd_symbol_status`, `kd_find_process_by_name`, `kd_find_process_by_name_raw`, `kd_list_threads`, `kd_list_threads_raw`, `kd_switch_process`, `kd_switch_thread`, `kd_stack`, and `kd_stack_process_thread`.

The Python helpers in `scripts/` are intentionally tracked and use only the Python standard library.
