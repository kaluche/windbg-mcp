# WinDbgMCP — C# MCP Server for Windows VM Control & Kernel Debugging

## What This Is

A single C# (.NET 8) MCP server that gives an LLM agent complete control over a Windows VM:
VM lifecycle, kernel debugging (DbgEng COM), guest execution (vmrun), and user-mode debugging (Frida/dbgsrv).

## Build & Run

```bash
# Build
dotnet build src/WinDbgMCP.Server/WinDbgMCP.Server.csproj

# Run (stdio mode — used by MCP clients)
dotnet run --project src/WinDbgMCP.Server/WinDbgMCP.Server.csproj

# Run tests
dotnet test src/WinDbgMCP.Tests/WinDbgMCP.Tests.csproj
```

**dotnet is at:** `C:\Program Files\dotnet\dotnet.exe`
If not in PATH, use: `"/c/Program Files/dotnet/dotnet.exe"` from bash.

## Project Structure

```
src/WinDbgMCP.Server/
├── Program.cs                    # Entry point, MCP server setup, DI
├── appsettings.json              # VM creds, KDNET config, timeouts
├── Configuration/
│   └── ServerConfig.cs           # Configuration model classes
├── State/
│   ├── SystemState.cs            # State model + enums
│   ├── StateCoordinator.cs       # Precondition gate (heart of system)
│   ├── ErrorMessages.cs          # LLM-friendly error catalog
│   └── ToolResult.cs             # Result type
├── Vmware/
│   ├── VmwareManager.cs          # vmrun wrapper (all VM operations)
│   └── ProcessResult.cs          # vmrun result types
├── KernelDebug/                  # Phase 2: DbgEng COM interop
├── GuestExec/                    # Phase 3: Guest command execution
├── UserModeDebug/                # Phase 4: Frida, dbgsrv, TTD
└── Tools/
    ├── VmTools.cs                # vm_start, vm_stop, vm_pause, etc.
    └── MetaTools.cs              # get_system_state
```

## Architecture Reference

Full architecture is in `architecture.md`. Key design principles:

1. **Every tool calls StateCoordinator.ValidatePreconditionsAsync() BEFORE executing**
2. **Every operation has a timeout** — no blocking calls anywhere
3. **Error messages tell the LLM exactly what to do next**
4. **Execution-control commands (g, t, p) are BLOCKED in kd_execute** — use kd_continue/kd_step
5. **BSOD is detected and handled differently from normal breakpoints**

## ABSOLUTE RULES

1. **Language is C# (.NET 8).** This is NOT a Python project. All code is C#.
2. **Never use pybag.** DbgEng COM is accessed directly via C# COM interop.
3. **Never modify files inside `.venv/` or `site-packages/`.** There is no Python venv.
4. **All MCP tools use `[McpServerTool]` attribute** from ModelContextProtocol SDK.
5. **All tools validate preconditions via StateCoordinator** before executing.
6. **appsettings.json contains secrets** — do not commit to public repos.

## VM Configuration

- **VMX:** `C:\Users\aviel\Documents\vm\VMs\Windows 11 x64.vmx`
- **Guest User:** Jeff123 / Jeff123!
- **KDNET:** port=50000, key=3cyy6s77i6u0h.2p05tab42qguu.3fqeye0rfyhac.dxcgplyny6qg
- **vmrun:** `C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe`

## Implementation Phases

- [x] Phase 1: Foundation — Project scaffold, config, state model, VmwareManager, MCP server, VM tools
- [ ] Phase 2: Kernel Debugging — DbgEng COM interop, thread affinity, event pump, kd_* tools
- [ ] Phase 3: Guest Execution — GuestExecManager, file transfer with quarantine, guest_* tools
- [ ] Phase 4: User-Mode Debugging — Frida, dbgsrv, TTD, x64dbg, umd_* tools
- [ ] Phase 5: Polish — Error messages, logging, recovery, setup scripts

## MCP Client Configuration

Add to your MCP client config (e.g., Claude Desktop):

```json
{
  "mcpServers": {
    "windbg-mcp": {
      "command": "C:\\Program Files\\dotnet\\dotnet.exe",
      "args": ["run", "--project", "C:\\Users\\aviel\\Desktop\\ClaudeProjects2\\windbg mcp servers\\new_mcp\\src\\WinDbgMCP.Server\\WinDbgMCP.Server.csproj"]
    }
  }
}
```
