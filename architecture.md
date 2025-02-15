# WinDbgMCP — Architecture & Implementation Plan

## AI-Driven Windows VM Control via MCP: Kernel Debugging, Guest Execution, and User-Mode Analysis

**Version:** 1.0 Draft
**Target Platform:** Windows Host → VMware Workstation Pro → Windows Guest

---

## Table of Contents

1. [System Overview & Goals](#1-system-overview--goals)
2. [Technology Stack Decisions](#2-technology-stack-decisions)
3. [Component Architecture](#3-component-architecture)
4. [The State Machine — Heart of the System](#4-the-state-machine)
5. [Layer 1: VM Management (VMware/vmrun)](#5-layer-1-vm-management)
6. [Layer 2: Kernel Debugging (DbgEng COM)](#6-layer-2-kernel-debugging)
7. [Layer 3: Guest Execution (VIX/vmrun)](#7-layer-3-guest-execution)
8. [Layer 4: User-Mode Debugging (Frida + dbgsrv + x64dbg)](#8-layer-4-user-mode-debugging)
9. [The Precondition Gate — Every Tool Gets Checked](#9-the-precondition-gate)
10. [Timeout Strategy](#10-timeout-strategy)
11. [Error Message Design (LLM-Oriented)](#11-error-message-design)
12. [Event Pump & Async Model](#12-event-pump--async-model)
13. [File Transfer Safety](#13-file-transfer-safety)
14. [VM Setup & Prerequisites](#14-vm-setup--prerequisites)
15. [MCP Tool Catalog (Complete)](#15-mcp-tool-catalog)
16. [Project Structure & Implementation Order](#16-project-structure--implementation-order)
17. [Edge Cases & Failure Modes](#17-edge-cases--failure-modes)
18. [Testing Strategy](#18-testing-strategy)
19. [Future Extensions](#19-future-extensions)

---

## 1. System Overview & Goals

### What We're Building

A single MCP server process running on a Windows host that gives an LLM agent complete control over a Windows VM for reverse engineering, malware analysis, kernel development, and vulnerability research. The agent can:

- Manage VM lifecycle (start, stop, snapshot, restore)
- Perform kernel debugging (break, step, read memory, set breakpoints, execute WinDbg commands)
- Execute commands and transfer files inside the guest OS
- Perform user-mode debugging (attach to processes, hook functions, record TTD traces)

### Core Design Principles

1. **The LLM must never block.** Every tool call returns within a bounded timeout. No exceptions.
2. **The LLM must never corrupt state.** Every tool call validates preconditions before executing.
3. **Error messages are prompts.** Every error tells the LLM exactly what to do next.
4. **State is always queryable.** The LLM can ask "what state is everything in?" at any time.
5. **No implicit side effects.** If the LLM calls `kd_break`, only a break happens. We don't silently resume, reconnect, or change state behind the scenes.

### What This Is NOT

- Not a GUI application — headless MCP server only
- Not multi-VM (v1 targets a single VM; multi-VM is a v2 concern)
- Not a replacement for WinDbg — it exposes WinDbg's engine programmatically
- Not a sandbox/detonation platform — it's an interactive analysis workbench

---

## 2. Technology Stack Decisions

### Language: C# (.NET 8+)

**Why C# over Python:**

- **DbgEng COM interop is native.** C# can declare COM interfaces with `[ComImport]` attributes and call them directly. Python requires ctypes/comtypes bridging through Pybag, adding a fragile translation layer.
- **The MCP C# SDK is co-maintained by Microsoft and Anthropic.** It's first-class, attribute-based (`[McpServerTool]`), and actively developed. NuGet package: `ModelContextProtocol`.
- **Thread safety for the event pump.** C# has `async/await`, `ConcurrentQueue<T>`, `SemaphoreSlim`, and `Channel<T>` built into the language. The DbgEng event pump thread and MCP request handling thread need careful synchronization — C# makes this manageable.
- **vmrun is just Process.Start.** Shelling out to vmrun is trivial in any language; the COM interop is what matters.
- **Single deployment target.** This only runs on Windows. No cross-platform concern.

**Why not Python:**

- Pybag (the best Python DbgEng wrapper) is a thin comtypes wrapper that doesn't handle all COM quirks (event callbacks, thread affinity). When something goes wrong at the COM boundary, debugging is painful.
- Python GIL complicates the event pump + MCP server threading model.
- FastMCP is excellent for simple tools but doesn't offer the same structured typing for complex state management.

### Hypervisor: VMware Workstation Pro

- Free for all users since November 2024
- `vmrun` CLI with 40+ commands for VM and guest operations
- Named pipe serial for kernel debug (trivial .vmx config)
- KDNET over VMware virtual network (preferred for Win8+)
- Industry standard for RE/malware analysis (FLARE VM, SANS, Mandiant)
- Snapshot/restore automation is reliable and fast

### Kernel Debug Transport: KDNET (primary) + Named Pipe Serial (fallback)

- **KDNET** for Windows 8+ guests: network speed, simpler BCD setup, Microsoft-recommended
- **Named pipe serial** for Windows 7 and earlier: 115200 baud, requires .vmx serial port config
- Both connect through the same DbgEng `IDebugClient::AttachKernel()` with different connection strings

### User-Mode Debug: Frida (primary) + dbgsrv (secondary) + x64dbg Automate (optional)

- **Frida**: non-breaking hooks, JavaScript instrumentation, API tracing — best for behavioral monitoring
- **dbgsrv.exe**: full WinDbg-class remote debugging from host — best for deep analysis
- **x64dbg Automate**: GUI-backed debugging with remote control — best for unpacking/manual RE

---

## 3. Component Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        LLM Agent                            │
│              (Claude, GPT, or any MCP client)                │
└──────────────────────────┬──────────────────────────────────┘
                           │ MCP Protocol (stdio or SSE)
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                    MCP Server Process                        │
│  ┌───────────────────────────────────────────────────────┐  │
│  │              MCP Tool Handler Layer                    │  │
│  │  [vm_*] [kd_*] [guest_*] [umd_*] [get_system_state]  │  │
│  └──────────────────────┬────────────────────────────────┘  │
│                         │                                    │
│  ┌──────────────────────▼────────────────────────────────┐  │
│  │            ★ STATE COORDINATOR ★                       │  │
│  │                                                        │  │
│  │  • Maintains authoritative system state                │  │
│  │  • Precondition gate for EVERY tool call               │  │
│  │  • Returns LLM-friendly errors on precondition failure │  │
│  │  • Serializes access to shared resources               │  │
│  │                                                        │  │
│  │  State includes:                                       │  │
│  │    - VM power state (off/running/paused/suspended)     │  │
│  │    - VMware Tools status (not installed/running/       │  │
│  │      not responding)                                   │  │
│  │    - Kernel debugger connection state                  │  │
│  │    - Kernel execution status (from GetExecutionStatus) │  │
│  │    - Pending WaitForEvent flag                         │  │
│  │    - Event queue (breakpoint hits, exceptions, etc.)   │  │
│  │    - Active Frida sessions                             │  │
│  │    - Active dbgsrv connections                         │  │
│  │    - File transfers in progress                        │  │
│  └──────┬──────────┬──────────┬──────────┬───────────────┘  │
│         │          │          │          │                    │
│  ┌──────▼───┐ ┌────▼────┐ ┌──▼─────┐ ┌─▼──────────────┐   │
│  │ VMware   │ │ DbgEng  │ │ Guest  │ │ User-Mode      │   │
│  │ Manager  │ │ Manager │ │ Exec   │ │ Debug Manager  │   │
│  │          │ │         │ │ Manager│ │                 │   │
│  │ vmrun    │ │ COM API │ │ vmrun  │ │ Frida/dbgsrv/  │   │
│  │ process  │ │ + Event │ │ guest  │ │ x64dbg         │   │
│  │ wrapper  │ │ Pump    │ │ ops    │ │                 │   │
│  └──────────┘ └─────────┘ └────────┘ └─────────────────┘   │
│                                                              │
└─────────────────────────────────────────────────────────────┘
         │              │           │              │
         ▼              ▼           ▼              ▼
    ┌─────────┐   ┌──────────┐  ┌─────┐    ┌───────────┐
    │ vmrun   │   │ Named    │  │ VIX │    │ TCP/Named │
    │ CLI     │   │ Pipe /   │  │ API │    │ Pipe to   │
    │         │   │ KDNET    │  │     │    │ Guest     │
    └────┬────┘   └────┬─────┘  └──┬──┘    └─────┬─────┘
         │             │           │              │
         ▼             ▼           ▼              ▼
    ┌─────────────────────────────────────────────────┐
    │              VMware Workstation Pro              │
    │  ┌───────────────────────────────────────────┐  │
    │  │           Windows Guest VM                 │  │
    │  │                                            │  │
    │  │  • Kernel debug enabled (KDNET/serial)     │  │
    │  │  • VMware Tools installed                  │  │
    │  │  • frida-server running (optional)         │  │
    │  │  • dbgsrv.exe running (optional)           │  │
    │  │  • FLARE VM tools installed (optional)     │  │
    │  └───────────────────────────────────────────┘  │
    └─────────────────────────────────────────────────┘
```

---

## 4. The State Machine — Heart of the System

This is the most critical section. Every bug, deadlock, and LLM confusion traces back to state management.

### 4.1 System State Model

```csharp
public class SystemState
{
    // === VM Layer ===
    public VmPowerState VmPower { get; set; }           // Off, Running, Paused, Suspended
    public VmToolsState VmTools { get; set; }           // NotInstalled, Running, NotResponding
    public string? VmIpAddress { get; set; }
    public string VmxPath { get; set; }

    // === Kernel Debug Layer ===
    public bool KdConnected { get; set; }               // Is DbgEng attached?
    public KdTransport KdTransportType { get; set; }    // KDNET, Serial, None
    public DebugExecutionStatus KdExecStatus { get; set; } // Break, Go, StepInto, StepOver, NoDebuggee
    public string? KdBreakReason { get; set; }          // "Breakpoint at nt!NtCreateFile+0x0"
    public bool KdWaitPending { get; set; }             // Is WaitForEvent in progress?
    public int PendingEventCount { get; set; }          // Queued events from event pump

    // BSOD detection — a BSOD break is fundamentally different from a normal
    // breakpoint: the OS is dead, not paused. Continuing is useless. Guest ops
    // are impossible. The LLM must be told to analyze (!analyze -v) or revert.
    public bool IsBugcheck { get; set; }                // Is the break due to a BSOD?
    public string? BugcheckCode { get; set; }           // e.g., "0x7E SYSTEM_THREAD_EXCEPTION_NOT_HANDLED"

    // === Guest Exec Layer ===
    public bool GuestOpsAvailable { get; set; }         // Derived: VmPower==Running && VmTools==Running && KdExecStatus!=Break
    public int ActiveTransfers { get; set; }

    // === User-Mode Debug Layer ===
    public FridaSessionState? FridaState { get; set; }  // null = not connected
    public DbgsrvSessionState? DbgsrvState { get; set; }
    public List<ActiveDebugSession> UserDebugSessions { get; set; }
}

public enum VmPowerState { Off, Running, Paused, Suspended, Unknown }
public enum VmToolsState { NotInstalled, Running, NotResponding, Unknown }
public enum KdTransport { None, KDNET, Serial }

// Maps directly to DEBUG_STATUS_* constants from dbgeng.h
public enum DebugExecutionStatus
{
    NoDebuggee       = 0,  // DEBUG_STATUS_NO_DEBUGGEE - not connected
    Break            = 6,  // DEBUG_STATUS_BREAK - target halted
    Go               = 1,  // DEBUG_STATUS_GO - target running
    StepInto         = 2,  // DEBUG_STATUS_STEP_INTO
    StepOver         = 3,  // DEBUG_STATUS_STEP_OVER
    StepBranch       = 4,  // DEBUG_STATUS_STEP_BRANCH
    GoHandled        = 7,  // DEBUG_STATUS_GO_HANDLED
    GoNotHandled     = 8,  // DEBUG_STATUS_GO_NOT_HANDLED
    Uninitialized    = -1  // Our own: DbgEng not loaded yet
}
```

### 4.2 State Transitions

```
VM Power State Machine:
═══════════════════════

                vm_start
    ┌─────┐ ──────────────► ┌─────────┐
    │ Off │                  │ Running │ ◄──── vm_resume
    └─────┘ ◄────────────── └────┬────┘
                vm_stop          │
                                 │ vm_pause
                                 ▼
                             ┌────────┐
                             │ Paused │
                             └────────┘

Kernel Debug State Machine:
═══════════════════════════

                    kd_connect
    ┌──────────┐ ──────────────► ┌───────┐
    │ NoTarget │                  │ Break │ ◄─── initial breakpoint
    └──────────┘ ◄────────────── └───┬───┘      kd_break (from Go)
                   kd_disconnect     │          breakpoint hit (event)
                                     │          exception (event)
                              kd_go  │
                            kd_step  │
                                     ▼
                                 ┌────┐
                                 │ Go │ ──── target running freely
                                 └────┘

Key Transitions & Their Effects:
────────────────────────────────

kd_break (while Go):
  → Sets interrupt flag via IDebugControl::SetInterrupt()
  → Event pump receives break event
  → State → Break
  → Guest ops become UNAVAILABLE (VM frozen)
  → Memory reads become AVAILABLE

kd_continue (while Break):
  → IDebugControl::SetExecutionStatus(DEBUG_STATUS_GO)
  → State → Go
  → Guest ops become AVAILABLE (after Tools resumes ~1-3s)
  → Memory reads become UNAVAILABLE

vm_pause (while Running):
  → vmrun pause
  → EVERYTHING freezes: kernel debugger, guest, network
  → Different from kd_break! DbgEng doesn't know about vmrun pause.
  → GetExecutionStatus may still report last known state
  → This is a trap — must track vm_pause independently

vm_snapshot_restore:
  → DESTROYS ALL STATE
  → Kernel debug connection: broken, must reconnect
  → Frida session: dead, must reattach
  → dbgsrv: dead, may need restart inside guest
  → Any pending WaitForEvent: returns error or hangs
  → MUST reset all state tracking to defaults
```

### 4.3 The Derived State Matrix

This matrix is the precondition check for every tool. The state coordinator evaluates this on every call.

```
┌────────────────────────┬───────┬───────┬───────┬────────┬──────────┬──────────┐
│ Tool                   │VM Off │VM Run │VM Run │VM Run  │VM Paused │  BSOD    │
│                        │       │KD Off │KD Brk │KD Go   │          │ (KD Brk) │
├────────────────────────┼───────┼───────┼───────┼────────┼──────────┼──────────┤
│ vm_start               │  ✅   │  ❌   │  ❌   │  ❌    │  ❌      │  ❌      │
│ vm_stop                │  ❌   │  ✅   │  ⚠️   │  ✅    │  ✅      │  ✅      │
│ vm_pause / vm_resume   │  ❌   │  ✅   │  ⚠️   │  ✅    │  ✅/✅   │  ⚠️     │
│ vm_snapshot_create     │  ✅   │  ✅   │  ✅   │  ✅    │  ✅      │  ✅      │
│ vm_snapshot_restore    │  ✅   │  ✅   │  ✅   │  ✅    │  ✅      │  ✅      │
│ vm_screenshot          │  ❌   │  ✅   │  ✅   │  ✅    │  ✅      │  ✅      │
├────────────────────────┼───────┼───────┼───────┼────────┼──────────┼──────────┤
│ kd_connect             │  ❌   │  ✅   │  ❌   │  ❌    │  ❌      │  ❌      │
│ kd_disconnect          │  ❌   │  ❌   │  ✅   │  ✅    │  ❌      │  ✅      │
│ kd_break               │  ❌   │  ❌   │  ❌   │  ✅    │  ❌      │  ❌      │
│ kd_continue            │  ❌   │  ❌   │  ✅   │  ❌    │  ❌      │  ❌ 🔵   │
│ kd_step                │  ❌   │  ❌   │  ✅   │  ❌    │  ❌      │  ❌ 🔵   │
│ kd_execute             │  ❌   │  ❌   │  ✅   │  ❌    │  ❌      │  ✅ **   │
│ kd_wait_for_event      │  ❌   │  ❌   │  ✅   │  ✅    │  ❌      │  ✅      │
├────────────────────────┼───────┼───────┼───────┼────────┼──────────┼──────────┤
│ guest_run_command      │  ❌   │  ✅   │  ❌   │  ✅    │  ❌      │  ❌ 🔵   │
│ guest_transfer_*       │  ❌   │  ✅   │  ❌   │  ✅    │  ❌      │  ❌ 🔵   │
│ guest_list_processes   │  ❌   │  ✅   │  ❌   │  ✅    │  ❌      │  ❌ 🔵   │
├────────────────────────┼───────┼───────┼───────┼────────┼──────────┼──────────┤
│ umd_frida_attach       │  ❌   │  ✅   │  ❌   │  ✅    │  ❌      │  ❌ 🔵   │
│ umd_dbgsrv_connect     │  ❌   │  ✅   │  ❌   │  ✅    │  ❌      │  ❌ 🔵   │
│ umd_ttd                │  ❌   │  ✅   │  ❌   │  ✅    │  ❌      │  ❌ 🔵   │
├────────────────────────┼───────┼───────┼───────┼────────┼──────────┼──────────┤
│ get_system_state       │  ✅   │  ✅   │  ✅   │  ✅    │  ✅      │  ✅      │
└────────────────────────┴───────┴───────┴───────┴────────┴──────────┴──────────┘

 ⚠️  = Allowed but warn: "Kernel debugger session will be lost"
 **  = kd_execute WORKS during BSOD — this is how you run !analyze -v
 🔵  = BSOD-specific error: "OS has crashed. Use !analyze -v or vm_snapshot_restore."
       Different from normal KD Break error ("call kd_continue to resume").
```

---

## 5. Layer 1: VM Management (VMware/vmrun)

### 5.1 VMware Manager Class

Wraps `vmrun` CLI. All operations are async with timeout.

```csharp
public class VmwareManager
{
    private readonly string _vmrunPath;  // "C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe"
    private readonly string _vmxPath;    // path to target .vmx file
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(30);

    // Power operations
    public Task<VmResult> StartAsync(bool headless = true, CancellationToken ct = default);
    public Task<VmResult> StopAsync(bool hard = false, CancellationToken ct = default);
    public Task<VmResult> PauseAsync(CancellationToken ct = default);
    public Task<VmResult> UnpauseAsync(CancellationToken ct = default);
    public Task<VmResult> ResetAsync(bool hard = false, CancellationToken ct = default);

    // Snapshot operations
    public Task<VmResult> SnapshotCreateAsync(string name, CancellationToken ct = default);
    public Task<VmResult> SnapshotRestoreAsync(string name, CancellationToken ct = default);
    public Task<VmResult> SnapshotDeleteAsync(string name, CancellationToken ct = default);
    public Task<SnapshotListResult> SnapshotListAsync(CancellationToken ct = default);

    // State queries
    public Task<VmPowerState> GetPowerStateAsync(CancellationToken ct = default);
    public Task<bool> AreToolsRunningAsync(TimeSpan? timeout = null, CancellationToken ct = default);
    public Task<string?> GetGuestIpAddressAsync(CancellationToken ct = default);
    public Task<VmResult> CaptureScreenAsync(string outputPath, CancellationToken ct = default);

    // Guest operations (delegated to GuestExecManager, but vmrun is the underlying tool)
    // See Layer 3

    // Internal: runs vmrun with timeout
    private async Task<ProcessResult> RunVmrunAsync(string args, TimeSpan? timeout, CancellationToken ct);
}
```

### 5.2 vmrun Process Execution

Every vmrun call MUST:

1. Have a timeout (default 30s for power ops, 60s for snapshot ops)
2. Capture both stdout and stderr
3. Kill the process on timeout
4. Parse exit codes (0 = success, nonzero = error with stderr message)

```csharp
private async Task<ProcessResult> RunVmrunAsync(string args, TimeSpan? timeout, CancellationToken ct)
{
    timeout ??= _defaultTimeout;
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(timeout.Value);

    var psi = new ProcessStartInfo
    {
        FileName = _vmrunPath,
        Arguments = args,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = new Process { StartInfo = psi };
    process.Start();

    var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
    var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

    try
    {
        await process.WaitForExitAsync(cts.Token);
        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
    catch (OperationCanceledException)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
        throw new TimeoutException($"vmrun timed out after {timeout.Value.TotalSeconds}s: {args}");
    }
}
```

### 5.3 VMware Tools Responsiveness Check

This is how we detect if the guest is actually alive before attempting guest operations:

```csharp
public async Task<bool> AreToolsRunningAsync(TimeSpan? timeout = null, CancellationToken ct = default)
{
    // vmrun checkToolsState returns "running", "installed", or "unknown"
    // But this can HANG if the guest is kernel-broken, so we use a short timeout.
    timeout ??= TimeSpan.FromSeconds(5);
    try
    {
        var result = await RunVmrunAsync(
            $"-T ws checkToolsState \"{_vmxPath}\"",
            timeout, ct);
        return result.Stdout.Trim().Equals("running", StringComparison.OrdinalIgnoreCase);
    }
    catch (TimeoutException)
    {
        return false; // Tools not responding
    }
}
```

### 5.4 Snapshot Restore State Reset

**Critical:** Snapshot restore invalidates ALL other state. The state coordinator must be notified.

```csharp
// In StateCoordinator:
public async Task<ToolResult> HandleSnapshotRestore(string snapshotName)
{
    // 1. Disconnect kernel debugger gracefully (if connected)
    if (_state.KdConnected)
    {
        await _dbgEngManager.DisconnectAsync();
        // Don't wait for clean disconnect — snapshot restore will nuke it anyway
    }

    // 2. Kill any Frida/dbgsrv sessions
    _fridaManager?.Dispose();
    _dbgsrvManager?.Dispose();

    // 3. Perform the restore
    var result = await _vmwareManager.SnapshotRestoreAsync(snapshotName);

    // 4. Reset ALL state to defaults
    _state = new SystemState
    {
        VmPower = VmPowerState.Running,  // VMware restores to running state
        VmTools = VmToolsState.Unknown,  // Need to re-probe
        KdConnected = false,
        KdExecStatus = DebugExecutionStatus.NoDebuggee,
        KdWaitPending = false,
        PendingEventCount = 0,
        FridaState = null,
        DbgsrvState = null
    };

    // 5. Wait for Tools to come back
    await WaitForToolsWithRetry(maxRetries: 10, delayMs: 2000);

    return ToolResult.Success(
        $"Snapshot '{snapshotName}' restored. All debug sessions disconnected. " +
        $"VM is running. VMware Tools: {_state.VmTools}. " +
        $"Call kd_connect to re-attach kernel debugger if needed.");
}
```

---

## 6. Layer 2: Kernel Debugging (DbgEng COM)

### 6.1 DbgEng COM Interface Declarations

These are the native COM interfaces from `dbgeng.h`, declared in C# for P/Invoke.

```csharp
// Entry point — loads dbgeng.dll and creates the initial client
[DllImport("dbgeng.dll")]
static extern int DebugCreate(ref Guid iid, out IntPtr iface);

// Core interfaces we need:
[ComImport, Guid("27fe5639-8407-4f47-8364-ee118fb08ac8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugClient
{
    int AttachKernel(uint flags, string connectOptions);
    int DetachCurrentProcess();
    int EndSession(uint flags);
    // ... other methods at correct vtable slots
}

[ComImport, Guid("5182e668-105e-416e-ad92-24ef800424ba"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugControl
{
    int GetExecutionStatus(out uint status);
    int SetExecutionStatus(uint status);
    int WaitForEvent(uint flags, uint timeout);
    int Execute(uint outputControl, string command, uint flags);
    int SetInterrupt(uint flags);
    int AddBreakpoint(uint type, uint desiredId, out IDebugBreakpoint bp);
    int GetNumberBreakpoints(out uint count);
    // ... other methods at correct vtable slots
}

[ComImport, Guid("88f7dfab-3ea7-4c3a-aefb-c4e8106173aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugDataSpaces
{
    int ReadVirtual(ulong offset, byte[] buffer, uint bufferSize, out uint bytesRead);
    int WriteVirtual(ulong offset, byte[] buffer, uint bufferSize, out uint bytesWritten);
    int ReadPhysical(ulong offset, byte[] buffer, uint bufferSize, out uint bytesRead);
    // ...
}

[ComImport, Guid("ce289126-9e84-45a7-937e-67bb18691493"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugRegisters
{
    int GetNumberRegisters(out uint count);
    int GetValue(uint register, out DEBUG_VALUE value);
    int SetValue(uint register, ref DEBUG_VALUE value);
    // ...
}

[ComImport, Guid("f2528316-0f1a-4431-aeed-11d096e1e2ab"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugSymbols
{
    int GetModuleByModuleName(string name, uint startIndex, out uint index, out ulong baseAddress);
    int GetNameByOffset(ulong offset, StringBuilder nameBuffer, uint nameBufferSize, out uint nameSize, out ulong displacement);
    int GetOffsetByName(string symbol, out ulong offset);
    // ...
}

// Event callbacks - we implement this interface
[ComImport, Guid("337be28b-5036-4d72-b6bf-c45fbb9f2eaa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugEventCallbacks
{
    int Breakpoint(IDebugBreakpoint bp);
    int Exception(ref EXCEPTION_RECORD64 exception, uint firstChance);
    int LoadModule(ulong imageFileHandle, ulong baseOffset, uint moduleSize, string moduleName, string imageName, uint checkSum, uint timeDateStamp);
    int UnloadModule(string imageBaseName, ulong baseOffset);
    int CreateProcess(/* params */);
    int ExitProcess(uint exitCode);
    int SessionStatus(uint status);
    int ChangeDebuggeeState(uint flags, ulong argument);
    int ChangeEngineState(uint flags, ulong argument);
    // ...
}
```

**Note on vtable ordering:** COM interface method declarations in C# MUST match the exact vtable order from the C/C++ header. Getting this wrong causes silent memory corruption or access violations. Each interface has many methods; we can stub unused ones with `int Placeholder_MethodName();` but they MUST be in the right order. Consider using the ClrDebug NuGet package which already has all interfaces correctly declared, or generate from the Windows SDK headers.

**Recommended approach:** Use the `ClrDebug` NuGet package which provides fully correct managed wrappers for all DbgEng interfaces, or generate interfaces using a COM type library importer.

### 6.2 DbgEng Manager Class

```csharp
public class DbgEngManager : IDisposable
{
    private IDebugClient _client;
    private IDebugControl _control;
    private IDebugDataSpaces _dataSpaces;
    private IDebugSymbols _symbols;
    private IDebugRegisters _registers;

    private readonly EventPump _eventPump;
    private readonly object _dbgEngLock = new();  // DbgEng is NOT thread-safe

    // === Connection ===
    public async Task<ConnectResult> ConnectKernelAsync(KdConnectionConfig config, CancellationToken ct);
    public Task DisconnectAsync();
    public bool IsConnected { get; }

    // === State Query (the critical one) ===
    public DebugExecutionStatus GetExecutionStatus()
    {
        if (!IsConnected) return DebugExecutionStatus.NoDebuggee;

        lock (_dbgEngLock)
        {
            int hr = _control.GetExecutionStatus(out uint status);
            if (hr != 0) return DebugExecutionStatus.Uninitialized;
            return (DebugExecutionStatus)status;
        }
    }

    // === Execution Control ===
    public Task<ToolResult> BreakAsync(TimeSpan timeout);
    public Task<ToolResult> ContinueAsync();
    public Task<ToolResult> StepIntoAsync(TimeSpan timeout);
    public Task<ToolResult> StepOverAsync(TimeSpan timeout);

    // === Memory ===
    public Task<MemoryReadResult> ReadVirtualMemoryAsync(ulong address, uint size);
    public Task<ToolResult> WriteVirtualMemoryAsync(ulong address, byte[] data);

    // === Command Execution ===
    public Task<CommandResult> ExecuteCommandAsync(string command, TimeSpan timeout);

    // === Breakpoints ===
    public Task<BreakpointResult> SetBreakpointAsync(string expression); // symbol or address
    public Task<ToolResult> RemoveBreakpointAsync(uint breakpointId);
    public Task<List<BreakpointInfo>> ListBreakpointsAsync();

    // === Info Queries ===
    public Task<StackTraceResult> GetStackTraceAsync(int maxFrames = 20);
    public Task<RegistersResult> GetRegistersAsync();
    public Task<List<ModuleInfo>> GetModulesAsync();
    public Task<string> GetCurrentProcessInfoAsync();
}
```

### 6.3 DbgEng Thread Affinity — CRITICAL

**DbgEng COM objects have thread affinity.** All calls to the DbgEng interfaces MUST happen from the same thread that called `DebugCreate()`. This is non-negotiable — calling from another thread causes access violations or silent corruption.

This means: the MCP tool handler (which runs on an async thread pool) cannot directly call `_control.Execute()`. Instead, we marshal all DbgEng calls to a dedicated thread.

```csharp
public class DbgEngThread : IDisposable
{
    private readonly Thread _thread;
    private readonly BlockingCollection<Func<object?>> _workQueue = new();
    private readonly CancellationTokenSource _cts = new();

    public DbgEngThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "DbgEng-Thread" };
        _thread.SetApartmentState(ApartmentState.MTA); // DbgEng requires MTA
        _thread.Start();
    }

    private void Run()
    {
        // ALL DbgEng COM creation and usage happens on this thread
        foreach (var work in _workQueue.GetConsumingEnumerable(_cts.Token))
        {
            work();
        }
    }

    public Task<T> ExecuteAsync<T>(Func<T> work, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<T>();
        var cts = new CancellationTokenSource(timeout);

        _workQueue.Add(() =>
        {
            try
            {
                if (cts.IsCancellationRequested)
                {
                    tcs.TrySetCanceled();
                    return null;
                }
                var result = work();
                tcs.TrySetResult(result);
                return null;
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                return null;
            }
        });

        return tcs.Task;
    }
}
```

### 6.4 Kernel Debug Connection

```csharp
public async Task<ConnectResult> ConnectKernelAsync(KdConnectionConfig config, CancellationToken ct)
{
    return await _dbgEngThread.ExecuteAsync(() =>
    {
        // 1. Create DbgEng client
        var guid = typeof(IDebugClient).GUID;
        int hr = DebugCreate(ref guid, out IntPtr clientPtr);
        _client = (IDebugClient)Marshal.GetObjectForIUnknown(clientPtr);

        // 2. QI for other interfaces
        _control = (IDebugControl)_client;
        _dataSpaces = (IDebugDataSpaces)_client;
        _symbols = (IDebugSymbols)_client;
        _registers = (IDebugRegisters)_client;

        // 3. Register event callbacks
        _client.SetEventCallbacks(_eventCallbacks);

        // 4. Register output callbacks (capture command output)
        _client.SetOutputCallbacks(_outputCallbacks);

        // 5. Build connection string
        string connStr = config.Transport switch
        {
            KdTransport.KDNET => $"net:port={config.Port},key={config.Key}",
            KdTransport.Serial => $"com:pipe,port={config.PipeName},resets=0,reconnect",
            _ => throw new ArgumentException("Unknown transport")
        };

        // 6. Attach — this returns quickly but isn't fully connected yet
        hr = _client.AttachKernel(DEBUG_ATTACH_KERNEL_CONNECTION, connStr);
        if (hr != 0) return ConnectResult.Failed($"AttachKernel failed: 0x{hr:X8}");

        // 7. Wait for initial breakpoint — THIS IS REQUIRED
        // Without this, the engine isn't fully initialized.
        // Use a timeout — if the target is already running, we may need
        // to break in manually.
        hr = _control.WaitForEvent(0, config.InitialTimeoutMs);  // e.g., 10000ms
        if (hr == 0)
        {
            return ConnectResult.Success("Connected. Target is at initial breakpoint.");
        }
        else if (hr == HR_TIMEOUT)
        {
            // Target is running — we're connected but not broken in
            return ConnectResult.SuccessRunning(
                "Connected to kernel debugger. Target is running freely. " +
                "Call kd_break to halt the target for inspection.");
        }
        else
        {
            return ConnectResult.Failed($"WaitForEvent failed: 0x{hr:X8}");
        }
    }, TimeSpan.FromSeconds(30));
}
```

### 6.5 Command Execution with Output Capture

WinDbg commands produce output via the IDebugOutputCallbacks interface. We capture it.

```csharp
public class OutputCapture : IDebugOutputCallbacks
{
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();

    public int Output(uint mask, string text)
    {
        lock (_lock) { _buffer.Append(text); }
        return 0; // S_OK
    }

    public string GetAndClear()
    {
        lock (_lock)
        {
            var result = _buffer.ToString();
            _buffer.Clear();
            return result;
        }
    }
}

// Usage in ExecuteCommandAsync:
public async Task<CommandResult> ExecuteCommandAsync(string command, TimeSpan timeout)
{
    return await _dbgEngThread.ExecuteAsync(() =>
    {
        // Precondition: must be in break state for most commands
        var status = GetExecutionStatus();
        if (status != DebugExecutionStatus.Break)
        {
            return CommandResult.Failed(
                $"Cannot execute command — target is in '{status}' state. " +
                "Call kd_break to halt the target first.");
        }

        _outputCapture.GetAndClear(); // Flush any stale output

        int hr = _control.Execute(
            DEBUG_OUTCTL_THIS_CLIENT,   // Output goes to our callbacks
            command,
            DEBUG_EXECUTE_DEFAULT);

        string output = _outputCapture.GetAndClear();

        if (hr != 0)
            return CommandResult.Failed($"Command failed (0x{hr:X8}): {output}");

        return CommandResult.Success(output);
    }, timeout);
}
```

### 6.6 Symbol Path Configuration

Symbols are essential. Configure on connect:

```csharp
// After successful AttachKernel:
string symbolPath =
    "srv*C:\\Symbols*https://msdl.microsoft.com/download/symbols;" +
    config.AdditionalSymbolPaths;  // User can add custom paths

_symbols.SetSymbolPath(symbolPath);

// Force reload
_control.Execute(DEBUG_OUTCTL_THIS_CLIENT, ".reload /f", DEBUG_EXECUTE_DEFAULT);
```

---

## 7. Layer 3: Guest Execution (VIX/vmrun)

### 7.1 Guest Execution Manager

```csharp
public class GuestExecManager
{
    private readonly VmwareManager _vmware;
    private readonly StateCoordinator _state;
    private readonly string _guestUsername;
    private readonly string _guestPassword;
    private readonly TimeSpan _defaultCmdTimeout = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _defaultTransferTimeout = TimeSpan.FromSeconds(120);

    // === Command Execution ===
    public async Task<GuestCommandResult> RunCommandAsync(
        string executable,
        string arguments = "",
        string? workingDirectory = null,
        bool interactive = false,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    public async Task<GuestCommandResult> RunScriptAsync(
        string interpreter,    // e.g., "C:\\Windows\\System32\\cmd.exe"
        string scriptText,     // e.g., "/c dir C:\\Users"
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    // === File Transfer ===
    public async Task<TransferResult> CopyFileToGuestAsync(
        string hostPath,
        string guestPath,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    public async Task<TransferResult> CopyFileFromGuestAsync(
        string guestPath,
        string hostPath,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    // === Process Management ===
    public async Task<List<GuestProcess>> ListProcessesAsync(CancellationToken ct = default);
    public async Task<ToolResult> KillProcessAsync(uint pid, CancellationToken ct = default);

    // === Filesystem ===
    public async Task<bool> FileExistsInGuestAsync(string guestPath, CancellationToken ct = default);
    public async Task<ToolResult> CreateDirectoryInGuestAsync(string guestPath, CancellationToken ct = default);
    public async Task<string> ReadFileInGuestAsync(string guestPath, CancellationToken ct = default);
}
```

### 7.2 Guest Command Execution Implementation

```csharp
public async Task<GuestCommandResult> RunCommandAsync(
    string executable,
    string arguments = "",
    string? workingDirectory = null,
    bool interactive = false,
    TimeSpan? timeout = null,
    CancellationToken ct = default)
{
    timeout ??= _defaultCmdTimeout;

    // === PRECONDITION CHECK (also done by StateCoordinator, but defense in depth) ===
    if (!await _vmware.AreToolsRunningAsync(TimeSpan.FromSeconds(3), ct))
    {
        return GuestCommandResult.Failed(
            "VMware Tools not responding. The VM may be frozen " +
            "(kernel debugger break?), powered off, or still booting. " +
            "Check get_system_state for details.");
    }

    // Build vmrun command
    // vmrun -gu <user> -gp <pass> runProgramInGuest <vmx> [-activeWindow] [-interactive] <program> [args]
    var argsBuilder = new StringBuilder();
    argsBuilder.Append($"-T ws -gu \"{_guestUsername}\" -gp \"{_guestPassword}\" ");
    argsBuilder.Append($"runProgramInGuest \"{_vmware.VmxPath}\" ");

    if (interactive) argsBuilder.Append("-interactive ");

    // For capturing stdout/stderr, we redirect inside the guest:
    // Run: cmd.exe /c "<executable> <arguments> > C:\temp\stdout.txt 2> C:\temp\stderr.txt"
    string stdoutFile = $"C:\\Windows\\Temp\\mcp_stdout_{Guid.NewGuid():N}.txt";
    string stderrFile = $"C:\\Windows\\Temp\\mcp_stderr_{Guid.NewGuid():N}.txt";

    string wrappedCmd;
    if (workingDirectory != null)
    {
        wrappedCmd = $"cmd.exe /c \"cd /d \"{workingDirectory}\" && " +
                     $"\"{executable}\" {arguments} > \"{stdoutFile}\" 2> \"{stderrFile}\"\"";
    }
    else
    {
        wrappedCmd = $"cmd.exe /c \"\"{executable}\" {arguments} " +
                     $"> \"{stdoutFile}\" 2> \"{stderrFile}\"\"";
    }

    argsBuilder.Append($"\"{wrappedCmd}\"");

    // Execute
    var result = await _vmware.RunVmrunAsync(argsBuilder.ToString(), timeout, ct);

    // Fetch stdout/stderr from guest
    string hostTempDir = Path.GetTempPath();
    string hostStdout = Path.Combine(hostTempDir, Path.GetRandomFileName());
    string hostStderr = Path.Combine(hostTempDir, Path.GetRandomFileName());

    try
    {
        await _vmware.CopyFileFromGuestAsync(stdoutFile, hostStdout, ct);
        await _vmware.CopyFileFromGuestAsync(stderrFile, hostStderr, ct);

        string stdout = File.Exists(hostStdout) ? await File.ReadAllTextAsync(hostStdout, ct) : "";
        string stderr = File.Exists(hostStderr) ? await File.ReadAllTextAsync(hostStderr, ct) : "";

        return new GuestCommandResult(result.ExitCode, stdout.Trim(), stderr.Trim());
    }
    finally
    {
        // Cleanup temp files on host and guest
        File.Delete(hostStdout);
        File.Delete(hostStderr);
        // Best-effort cleanup in guest (don't fail if this times out)
        try
        {
            await RunCommandAsync("cmd.exe", $"/c del \"{stdoutFile}\" \"{stderrFile}\"",
                timeout: TimeSpan.FromSeconds(5), ct: ct);
        }
        catch { }
    }
}
```

### 7.3 vmrun Guest Operations Reference

```
# Run a program inside the guest
vmrun -T ws -gu user -gp pass runProgramInGuest "path.vmx" "C:\Windows\notepad.exe"
vmrun -T ws -gu user -gp pass runProgramInGuest "path.vmx" -activeWindow "C:\tool.exe" "arg1"
vmrun -T ws -gu user -gp pass runScriptInGuest "path.vmx" "C:\Windows\System32\cmd.exe" "/c dir"

# File copy
vmrun -T ws -gu user -gp pass copyFileFromHostToGuest "path.vmx" "C:\host\file.exe" "C:\guest\file.exe"
vmrun -T ws -gu user -gp pass copyFileFromGuestToHost "path.vmx" "C:\guest\output.txt" "C:\host\output.txt"

# Process management
vmrun -T ws -gu user -gp pass listProcessesInGuest "path.vmx"
vmrun -T ws -gu user -gp pass killProcessInGuest "path.vmx" <pid>

# File operations
vmrun -T ws -gu user -gp pass fileExistsInGuest "path.vmx" "C:\path\file.txt"
vmrun -T ws -gu user -gp pass directoryExistsInGuest "path.vmx" "C:\path"
vmrun -T ws -gu user -gp pass createDirectoryInGuest "path.vmx" "C:\new\dir"
vmrun -T ws -gu user -gp pass deleteFileInGuest "path.vmx" "C:\path\file.txt"

# State
vmrun -T ws checkToolsState "path.vmx"   # Returns: "running", "installed", or "unknown"
vmrun -T ws getGuestIPAddress "path.vmx"
```

---

## 8. Layer 4: User-Mode Debugging (Frida + dbgsrv + x64dbg)

### 8.1 Frida Integration

Frida runs as `frida-server.exe` inside the VM. The MCP server connects via Frida's Python or Node.js client over TCP. Since our MCP server is C#, we have three options:

**Option A (Recommended): Frida CLI wrapper**
Shell out to `frida` CLI or `frida-tools` Python scripts from C#, capture output.

**Option B: Frida C bindings via P/Invoke**
Use frida-core's C API directly. Complex but performant.

**Option C: Sidecar Python process**
Run a small Python FastMCP server just for Frida tools, composing it with the main C# MCP server. MCP supports server composition.

**Recommendation: Start with Option A (CLI wrapper), migrate to Option C if needed.**

```csharp
public class FridaManager
{
    private readonly string _fridaPath;         // Path to frida.exe on host
    private readonly string _vmIpAddress;
    private readonly int _fridaPort = 27042;    // Default frida-server port

    // === Session Management ===
    public async Task<FridaResult> AttachAsync(int pid, CancellationToken ct);
    public async Task<FridaResult> AttachByNameAsync(string processName, CancellationToken ct);
    public async Task<FridaResult> SpawnAsync(string programPath, string[] args, CancellationToken ct);
    public async Task DetachAsync();

    // === Instrumentation ===
    public async Task<FridaResult> InjectScriptAsync(string jsCode, CancellationToken ct);
    public async Task<FridaResult> HookFunctionAsync(string moduleName, string functionName, string onEnterJs, string? onLeaveJs, CancellationToken ct);
    public async Task<FridaResult> TraceCallsAsync(string[] functions, CancellationToken ct);
    public async Task<FridaResult> InterceptImportAsync(string moduleName, string importName, string replacementJs, CancellationToken ct);

    // === Memory ===
    public async Task<byte[]> ReadMemoryAsync(ulong address, uint size, CancellationToken ct);
    public async Task WriteMemoryAsync(ulong address, byte[] data, CancellationToken ct);
    public async Task<List<MemoryRange>> ScanMemoryAsync(string pattern, CancellationToken ct);

    // === Enumeration ===
    public async Task<List<FridaModule>> EnumerateModulesAsync(CancellationToken ct);
    public async Task<List<FridaExport>> EnumerateExportsAsync(string moduleName, CancellationToken ct);
    public async Task<List<FridaImport>> EnumerateImportsAsync(string moduleName, CancellationToken ct);
}
```

### 8.2 dbgsrv Remote User-Mode Debugging

`dbgsrv.exe` runs inside the VM as a lightweight process server. The full debugger engine runs on the host, connecting remotely. This uses the SAME DbgEng COM API as kernel debugging, but with `ConnectProcessServer` instead of `AttachKernel`.

```csharp
public class DbgsrvManager
{
    private IDebugClient _userClient;   // Separate from kernel debug client!
    private IDebugControl _userControl;
    private IDebugDataSpaces _userDataSpaces;

    public async Task<ConnectResult> ConnectAsync(string vmIpAddress, int port = 5064)
    {
        return await _dbgEngThread.ExecuteAsync(() =>
        {
            // Create a NEW DbgEng client (separate from kernel debug)
            var guid = typeof(IDebugClient).GUID;
            DebugCreate(ref guid, out IntPtr clientPtr);
            _userClient = (IDebugClient)Marshal.GetObjectForIUnknown(clientPtr);

            // Connect to remote process server
            string connStr = $"tcp:port={port},server={vmIpAddress}";
            ulong serverId;
            int hr = _userClient.ConnectProcessServer(connStr, out serverId);
            if (hr != 0) return ConnectResult.Failed($"ConnectProcessServer failed: 0x{hr:X8}");

            _processServerId = serverId;
            return ConnectResult.Success("Connected to dbgsrv.");
        }, TimeSpan.FromSeconds(15));
    }

    public async Task<AttachResult> AttachToProcessAsync(uint pid)
    {
        return await _dbgEngThread.ExecuteAsync(() =>
        {
            int hr = _userClient.AttachProcess(_processServerId, pid, DEBUG_ATTACH_DEFAULT);
            if (hr != 0) return AttachResult.Failed($"AttachProcess failed: 0x{hr:X8}");

            // Wait for initial break
            hr = _userControl.WaitForEvent(0, 10000);
            if (hr != 0) return AttachResult.Failed("WaitForEvent failed after attach");

            return AttachResult.Success($"Attached to PID {pid}.");
        }, TimeSpan.FromSeconds(15));
    }

    // Same IDebugControl/IDebugDataSpaces/IDebugSymbols methods as kernel debug
    // but operating on a user-mode process
}
```

**Important: dbgsrv user-mode debugging and kernel debugging use separate DbgEng client instances.** You CAN have both active simultaneously — the kernel debugger halts the entire OS (including the debugged process), while the user-mode debugger only controls one process. But if the kernel debugger breaks in, the dbgsrv connection will appear unresponsive until the kernel resumes.

### 8.3 x64dbg Automate Integration

x64dbg Automate provides remote control via a native plugin that exposes a TCP API.

```csharp
public class X64DbgManager
{
    // Uses the x64dbg-automate protocol over TCP
    // Plugin must be installed in x64dbg inside the VM

    private readonly string _vmIpAddress;
    private readonly int _port = 27043;  // x64dbg-automate default

    public async Task<X64Result> AttachAsync(int pid, CancellationToken ct);
    public async Task<X64Result> OpenFileAsync(string path, CancellationToken ct);
    public async Task<X64Result> SetBreakpointAsync(ulong address, CancellationToken ct);
    public async Task<X64Result> StepIntoAsync(CancellationToken ct);
    public async Task<X64Result> StepOverAsync(CancellationToken ct);
    public async Task<X64Result> RunAsync(CancellationToken ct);
    public async Task<X64Result> ExecuteCommandAsync(string command, CancellationToken ct);
    public async Task<byte[]> ReadMemoryAsync(ulong address, uint size, CancellationToken ct);
    public async Task<List<X64Module>> GetModulesAsync(CancellationToken ct);
    public async Task<X64Result> GetRegistersAsync(CancellationToken ct);
}
```

### 8.4 Time Travel Debugging (TTD)

TTD records entire process execution for later replay. Automatable via `ttd.exe` CLI inside the VM.

```csharp
public class TtdManager
{
    private readonly GuestExecManager _guest;

    // Record a trace by launching a process under TTD
    public async Task<TtdResult> RecordLaunchAsync(string targetPath, string args, string outputDir, CancellationToken ct)
    {
        // TTD.exe -launch <target> -out <dir> runs inside the guest
        // This is a long-running operation — the recording continues until the process exits
        // or we stop it.
        return await _guest.RunCommandAsync(
            "C:\\ttd\\TTD.exe",
            $"-accepteula -launch \"{targetPath}\" {args} -out \"{outputDir}\"",
            timeout: TimeSpan.FromMinutes(5),
            ct: ct);
    }

    // Record by attaching to existing process
    public async Task<TtdResult> RecordAttachAsync(uint pid, string outputDir, CancellationToken ct)
    {
        return await _guest.RunCommandAsync(
            "C:\\ttd\\TTD.exe",
            $"-accepteula -attach {pid} -out \"{outputDir}\"",
            timeout: TimeSpan.FromMinutes(5),
            ct: ct);
    }

    // Retrieve .run file from guest to host for analysis
    public async Task<TransferResult> RetrieveTraceAsync(string guestTracePath, string hostOutputPath, CancellationToken ct)
    {
        return await _guest.CopyFileFromGuestAsync(guestTracePath, hostOutputPath, ct: ct);
    }

    // Open and query trace using WinDbg/DbgEng on host
    // The .run file is opened as a dump target
    public async Task<CommandResult> QueryTraceAsync(string hostTracePath, string ttdQuery, CancellationToken ct)
    {
        // Open trace in a separate DbgEng session
        // Execute TTD queries like:
        // dx @$cursession.TTD.Calls("kernel32!CreateFileW")
        // dx @$cursession.TTD.Memory(0x12345, 0x12345+4, "w")
        // ...
    }
}
```

---

## 9. The Precondition Gate — Every Tool Gets Checked

### 9.1 Architecture

EVERY MCP tool call flows through this single validation method before executing.

```csharp
public class StateCoordinator
{
    private SystemState _state;
    private readonly VmwareManager _vmware;
    private readonly DbgEngManager _dbgEng;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Called BEFORE every MCP tool execution.
    /// Returns null if preconditions are met, or a ToolResult with an error message if not.
    /// </summary>
    public async Task<ToolResult?> ValidatePreconditionsAsync(string toolName, Dictionary<string, object>? args = null)
    {
        // 1. Refresh state (cheap — GetExecutionStatus is a single COM call)
        await RefreshStateAsync();

        // 2. Check tool-specific preconditions
        return toolName switch
        {
            // --- VM tools ---
            "vm_start"              => RequireVmOff(),
            "vm_stop"               => RequireVmNotOff(warnIfKdAttached: true),
            "vm_pause"              => RequireVmRunning(warnIfKdAttached: true),
            "vm_resume"             => RequireVmPaused(),
            "vm_snapshot_create"    => null,  // Always allowed
            "vm_snapshot_restore"   => null,  // Always allowed (but resets everything)
            "vm_screenshot"         => RequireVmNotOff(),
            "vm_snapshot_list"      => null,  // Always allowed

            // --- Kernel debug tools (consolidated) ---
            "kd_connect"            => RequireVmRunning_KdNotConnected(),
            "kd_disconnect"         => RequireKdConnected(),
            "kd_break"              => RequireKdConnected_TargetRunning(),
            "kd_continue"           => RequireKdConnected_TargetBroken_CanResume(),
            "kd_step"               => RequireKdConnected_TargetBroken_NoWaitPending(),
            "kd_execute"            => RequireKdConnected_TargetBroken(),
            "kd_wait_for_event"     => RequireKdConnected(),  // Works in any kd state

            // --- Guest tools ---
            "guest_run_command"         => RequireGuestOpsAvailable(),
            "guest_transfer_to_vm"      => RequireGuestOpsAvailable(),
            "guest_transfer_from_vm"    => RequireGuestOpsAvailable(),
            "guest_list_processes"      => RequireGuestOpsAvailable(),
            "guest_kill_process"        => RequireGuestOpsAvailable(),

            // --- User-mode debug tools ---
            "umd_frida_attach"          => RequireGuestOpsAvailable(),
            "umd_frida"                 => RequireFridaAttached(),
            "umd_dbgsrv_connect"        => RequireGuestOpsAvailable(),
            "umd_dbgsrv_execute"        => RequireDbgsrvConnected(),
            "umd_ttd"                   => RequireGuestOpsAvailable(),
            "umd_ttd_query"             => null,  // Operates on host-side trace files

            // --- Meta tools ---
            "get_system_state"      => null,  // ALWAYS allowed
            _                       => null   // Unknown tools pass through
        };
    }
}
```

### 9.2 Precondition Check Implementations

```csharp
private ToolResult? RequireVmRunning()
{
    if (_state.VmPower != VmPowerState.Running)
        return ToolResult.Error(
            $"VM is {_state.VmPower}. Start the VM first with vm_start.");
    return null;
}

private ToolResult? RequireKdConnected_TargetBroken()
{
    if (!_state.KdConnected)
        return ToolResult.Error(
            "Kernel debugger is not connected. Call kd_connect first.");

    if (_state.KdExecStatus != DebugExecutionStatus.Break)
        return ToolResult.Error(
            $"Target is in '{_state.KdExecStatus}' state — cannot read memory or execute " +
            "commands while the target is running. Call kd_break to halt the target first.");

    if (_state.KdWaitPending)
        return ToolResult.Error(
            "A previous step/continue operation is still pending. " +
            "Call kd_get_events to check if it completed, or kd_break to interrupt it.");

    return null;
}

private ToolResult? RequireKdConnected_TargetRunning()
{
    if (!_state.KdConnected)
        return ToolResult.Error(
            "Kernel debugger is not connected. Call kd_connect first.");

    if (_state.KdExecStatus == DebugExecutionStatus.Break)
    {
        if (_state.IsBugcheck)
            return ToolResult.Error(
                $"🔵 BSOD — Bugcheck {_state.BugcheckCode ?? "unknown"}. "
                + "Target is halted at a bugcheck, not running. The OS has crashed. "
                + "Use kd_execute('!analyze -v') to investigate, then "
                + "vm_snapshot_restore to recover.");

        return ToolResult.Error(
            "Target is already halted at a breakpoint. "
            + "You can inspect state with kd_execute, "
            + "or call kd_continue to resume execution first.");
    }

    return null;
}

/// <summary>
/// For kd_continue: target must be broken AND not in a BSOD
/// (resuming a bugchecked OS is pointless / hangs).
/// </summary>
private ToolResult? RequireKdConnected_TargetBroken_CanResume()
{
    if (!_state.KdConnected)
        return ToolResult.Error(
            "Kernel debugger is not connected. Call kd_connect first.");

    if (_state.KdExecStatus != DebugExecutionStatus.Break)
        return ToolResult.Error(
            $"Target is in '{_state.KdExecStatus}' state — already running. "
            + "Call kd_break to halt it first, or kd_wait_for_event to "
            + "wait for a breakpoint hit.");

    if (_state.IsBugcheck)
        return ToolResult.Error(
            $"🔵 BSOD — Bugcheck {_state.BugcheckCode ?? "unknown"}. "
            + "The OS has crashed and cannot be meaningfully resumed. "
            + "Continuing will likely re-enter the bugcheck handler or hang. "
            + "Options: "
            + "(1) kd_execute('!analyze -v') to analyze the crash. "
            + "(2) vm_snapshot_restore('Clean-Ready') to revert to a clean state. "
            + "(3) vm_stop(hard=true) then vm_start to reboot.");

    if (_state.KdWaitPending)
        return ToolResult.Error(
            "A previous step/continue operation has a pending WaitForEvent. "
            + "Call kd_wait_for_event to check if it completed, or kd_break to interrupt.");

    return null;
}

private ToolResult? RequireGuestOpsAvailable()
{
    if (_state.VmPower != VmPowerState.Running)
        return ToolResult.Error(
            $"VM is {_state.VmPower}. Cannot execute guest operations. Start the VM with vm_start.");

    if (_state.VmPower == VmPowerState.Paused)
        return ToolResult.Error(
            "VM is paused. Call vm_resume before running guest commands.");

    // THE CRITICAL CHECK: is the kernel debugger holding the VM frozen?
    if (_state.KdConnected && _state.KdExecStatus == DebugExecutionStatus.Break)
    {
        if (_state.IsBugcheck)
            return ToolResult.Error(
                $"🔵 BSOD DETECTED — Bugcheck {_state.BugcheckCode ?? "unknown"}. "
                + "The guest OS has crashed. Guest operations will NOT work because "
                + "the OS is dead (not just paused). "
                + "Options: (1) kd_execute('!analyze -v') to analyze the crash, "
                + "(2) vm_snapshot_restore to revert to a clean state, "
                + "(3) vm_stop(hard=true) + vm_start to reboot.");

        return ToolResult.Error(
            "VM is frozen — kernel debugger is at a breakpoint. "
            + "Guest operations (commands, file transfers) require the OS to be running. "
            + "Call kd_continue to resume the target, then retry this operation.");
    }

    if (_state.VmTools != VmToolsState.Running)
        return ToolResult.Error(
            $"VMware Tools status: {_state.VmTools}. Guest operations require VMware Tools "
            + "to be running inside the VM. The VM may still be booting, or Tools may need "
            + "to be installed. Wait a moment and check get_system_state.");

    return null;
}

private ToolResult? RequireKdConnected_TargetBroken_NoWaitPending()
{
    var baseCheck = RequireKdConnected_TargetBroken();
    if (baseCheck != null) return baseCheck;

    if (_state.KdWaitPending)
        return ToolResult.Error(
            "Cannot step — a previous WaitForEvent is still in progress. " +
            "The previous step may not have completed yet. " +
            "Call kd_get_events to check status, or kd_break to interrupt.");

    return null;
}
```

### 9.3 State Refresh

Called before every precondition check. Must be FAST.

```csharp
private async Task RefreshStateAsync()
{
    // 1. DbgEng execution status — single COM call, ~microseconds
    if (_state.KdConnected && _dbgEng.IsConnected)
    {
        _state.KdExecStatus = _dbgEng.GetExecutionStatus();

        // If DbgEng reports NoDebuggee but we thought we were connected,
        // the connection was lost (e.g., VM rebooted, snapshot restored)
        if (_state.KdExecStatus == DebugExecutionStatus.NoDebuggee)
        {
            _state.KdConnected = false;
            _state.KdBreakReason = null;
        }
    }

    // 2. Event queue count
    _state.PendingEventCount = _eventPump?.PendingCount ?? 0;

    // 2.5 BSOD detection — if we're in Break state, check if it's a bugcheck.
    //     We only re-check when transitioning INTO break state (not every refresh)
    //     to avoid repeated expensive command execution.
    if (_state.KdConnected && _state.KdExecStatus == DebugExecutionStatus.Break
        && !_bsodCheckedForCurrentBreak)
    {
        _bsodCheckedForCurrentBreak = true;
        await DetectBugcheckAsync();
    }
    else if (_state.KdExecStatus != DebugExecutionStatus.Break)
    {
        // Target is running — clear BSOD state
        _state.IsBugcheck = false;
        _state.BugcheckCode = null;
        _bsodCheckedForCurrentBreak = false;
    }

    // 3. VM power state — only refresh if stale (>2 seconds old)
    //    vmrun list is slow (~500ms), don't call on every tool invocation
    if (DateTime.UtcNow - _lastVmStateRefresh > TimeSpan.FromSeconds(2))
    {
        _state.VmPower = await _vmware.GetPowerStateAsync();
        _lastVmStateRefresh = DateTime.UtcNow;
    }

    // 4. Tools status — only if VM is running and not kernel-broken
    //    checkToolsState can hang if VM is frozen, so skip in that case
    if (_state.VmPower == VmPowerState.Running &&
        _state.KdExecStatus != DebugExecutionStatus.Break &&
        DateTime.UtcNow - _lastToolsRefresh > TimeSpan.FromSeconds(5))
    {
        _state.VmTools = await _vmware.AreToolsRunningAsync(TimeSpan.FromSeconds(3))
            ? VmToolsState.Running
            : VmToolsState.NotResponding;
        _lastToolsRefresh = DateTime.UtcNow;
    }

    // 5. Derive compound states
    _state.GuestOpsAvailable =
        _state.VmPower == VmPowerState.Running &&
        _state.VmTools == VmToolsState.Running &&
        (!_state.KdConnected || _state.KdExecStatus != DebugExecutionStatus.Break);
}
```

---

## 10. Timeout Strategy

### 10.1 Timeout Defaults by Operation Category

```csharp
public static class Timeouts
{
    // VM operations
    public static readonly TimeSpan VmStart = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan VmStop = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan VmPauseResume = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan VmSnapshotCreate = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan VmSnapshotRestore = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan VmScreenshot = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan VmToolsCheck = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan VmGetIp = TimeSpan.FromSeconds(10);

    // Kernel debug operations
    public static readonly TimeSpan KdConnect = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan KdInitialBreak = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan KdBreak = TimeSpan.FromSeconds(10);       // SetInterrupt + WaitForEvent
    public static readonly TimeSpan KdStep = TimeSpan.FromSeconds(10);        // Step + WaitForEvent
    public static readonly TimeSpan KdCommandExecute = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan KdMemoryRead = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan KdMemoryWrite = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan KdWaitForBreakpoint = TimeSpan.FromSeconds(10); // Short default!

    // Guest operations
    public static readonly TimeSpan GuestCommand = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan GuestFileTransfer = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan GuestListProcesses = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan GuestKillProcess = TimeSpan.FromSeconds(10);

    // User-mode debug
    public static readonly TimeSpan FridaAttach = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan FridaScript = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DbgsrvConnect = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan TtdRecord = TimeSpan.FromMinutes(5);
}
```

### 10.2 Every Tool Gets a Timeout

The MCP tool handler wraps every operation:

```csharp
private async Task<ToolResult> ExecuteWithTimeoutAsync(
    string toolName,
    Func<CancellationToken, Task<ToolResult>> operation,
    TimeSpan timeout,
    CancellationToken ct)
{
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(timeout);

    try
    {
        return await operation(cts.Token);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        // Our timeout fired, not the caller's cancellation
        return ToolResult.Error(
            $"{toolName} timed out after {timeout.TotalSeconds}s. " +
            "The operation may still be in progress. " +
            "Call get_system_state to check current status before retrying.");
    }
    catch (Exception ex)
    {
        return ToolResult.Error(
            $"{toolName} failed with error: {ex.Message}. " +
            "Call get_system_state to verify system is in a consistent state.");
    }
}
```

### 10.3 Timeout on WaitForEvent Specifically

The `kd_continue` (go and wait for next break) pattern:

```csharp
// kd_continue: resume execution and return immediately
// Does NOT wait for next break — that's what kd_wait_for_event is for
public async Task<ToolResult> ContinueAsync()
{
    return await _dbgEngThread.ExecuteAsync(() =>
    {
        int hr = _control.SetExecutionStatus(DEBUG_STATUS_GO);
        if (hr != 0)
            return ToolResult.Failed($"SetExecutionStatus(GO) failed: 0x{hr:X8}");

        _state.KdExecStatus = DebugExecutionStatus.Go;
        return ToolResult.Success(
            "Target resumed. Guest operations are now available. " +
            "If you set breakpoints, call kd_wait_for_event to check for hits, " +
            "or call kd_break to halt the target manually.");
    }, Timeouts.KdCommandExecute);
}

// kd_wait_for_event: poll for debug events with a SHORT timeout
// LLM calls this repeatedly if it's waiting for a breakpoint
public async Task<EventResult> WaitForEventAsync(TimeSpan? timeout = null)
{
    timeout ??= Timeouts.KdWaitForBreakpoint;  // 10 seconds default

    return await _dbgEngThread.ExecuteAsync(() =>
    {
        int hr = _control.WaitForEvent(0, (uint)timeout.Value.TotalMilliseconds);

        if (hr == 0)
        {
            // Event received!
            _state.KdExecStatus = DebugExecutionStatus.Break;
            var reason = GetBreakReasonString();
            return EventResult.EventReceived(reason);
        }
        else if (hr == HR_TIMEOUT)
        {
            return EventResult.Timeout(
                $"No debug event received within {timeout.Value.TotalSeconds}s. " +
                "Target is still running. You can: " +
                "(1) Call kd_wait_for_event again to keep waiting, " +
                "(2) Call kd_break to manually halt the target, or " +
                "(3) Proceed with guest operations while the target runs.");
        }
        else
        {
            return EventResult.Error($"WaitForEvent returned 0x{hr:X8}");
        }
    }, timeout.Value + TimeSpan.FromSeconds(2));  // Slightly larger than WaitForEvent timeout
}
```

---

## 11. Error Message Design (LLM-Oriented)

### 11.1 Error Message Template

Every error message follows this structure:

```
[WHAT HAPPENED] — [WHY IT HAPPENED] — [WHAT TO DO NEXT]
```

### 11.2 Complete Error Message Catalog

```csharp
public static class ErrorMessages
{
    // === VM State Errors ===
    public const string VmIsOff =
        "VM is powered off. Call vm_start to boot the VM before performing this operation.";

    public const string VmIsPaused =
        "VM is paused (via vm_pause). Call vm_resume to unpause, then retry.";

    public const string VmAlreadyRunning =
        "VM is already running. No action needed — you can proceed with other operations.";

    // === Kernel Debug State Errors ===
    public const string KdNotConnected =
        "Kernel debugger is not connected. Call kd_connect to attach to the target VM's kernel.";

    public const string KdAlreadyConnected =
        "Kernel debugger is already connected. Call kd_disconnect first if you need to reconnect.";

    public const string TargetNotBroken =
        "Cannot inspect target — it is currently running freely. " +
        "Memory reads, register dumps, and stack traces require the target to be halted. " +
        "Call kd_break to halt the target, then retry.";

    public const string TargetAlreadyBroken =
        "Target is already halted at a breakpoint. " +
        "You can inspect state with kd_execute (e.g., 'k', 'r', 'db addr'), " +
        "or resume execution (kd_continue).";

    public const string WaitPending =
        "A previous step or continue operation has a pending WaitForEvent. " +
        "Call kd_get_events to check if it completed, or kd_break to interrupt it.";

    // === Guest Operation Errors ===
    public const string GuestFrozenByKd =
        "VM is frozen — kernel debugger is at a breakpoint. " +
        "The entire guest OS is halted, so commands and file transfers will hang. " +
        "Call kd_continue to resume the target, wait 2-3 seconds for VMware Tools " +
        "to recover, then retry this guest operation.";

    public const string ToolsNotResponding =
        "VMware Tools is not responding inside the guest. Possible causes: " +
        "(1) VM is still booting — wait 10-30 seconds and retry. " +
        "(2) Guest OS crashed — check vm_screenshot. " +
        "(3) VMware Tools not installed — cannot execute guest operations without it. " +
        "Call get_system_state for current status.";

    // === Timeout Errors ===
    public static string OperationTimedOut(string operation, double seconds) =>
        $"{operation} timed out after {seconds}s. The operation may still be in progress. " +
        "Call get_system_state to check current status before retrying.";

    // === Connection Errors ===
    public const string KdConnectFailed =
        "Failed to connect kernel debugger. Verify: " +
        "(1) VM is running with debug boot configuration enabled. " +
        "(2) KDNET port/key or serial pipe name is correct. " +
        "(3) No other debugger is already attached to this target.";

    public const string SnapshotRestoredWarning =
        "Snapshot restored successfully. WARNING: All debug sessions have been invalidated. " +
        "Kernel debugger: disconnected. Frida sessions: terminated. dbgsrv: disconnected. " +
        "You must re-establish any debug sessions you need.";
}
```

---

## 12. Event Pump & Async Model

### 12.1 The Event Pump Thread

A dedicated thread that continuously processes DbgEng events.

```csharp
public class EventPump : IDisposable
{
    private readonly IDebugControl _control;
    private readonly ConcurrentQueue<DebugEvent> _eventQueue = new();
    private readonly Thread _pumpThread;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _isWaiting = false;

    public int PendingCount => _eventQueue.Count;

    public EventPump(IDebugControl control)
    {
        _control = control;
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "DbgEng-EventPump"
        };
    }

    public void Start() => _pumpThread.Start();

    private void PumpLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            _isWaiting = true;

            // Wait for a short interval — don't block forever
            int hr = _control.WaitForEvent(0, 1000);  // 1 second poll

            _isWaiting = false;

            if (hr == 0)
            {
                // Event received — our IDebugEventCallbacks already processed it
                // and enqueued a DebugEvent. The callback does:
                //   _eventQueue.Enqueue(new DebugEvent { Type = ..., Details = ... });
            }
            else if (hr == HR_TIMEOUT)
            {
                // No event — loop and wait again
                continue;
            }
            else
            {
                // Error or target disconnected
                _eventQueue.Enqueue(new DebugEvent
                {
                    Type = DebugEventType.Error,
                    Details = $"WaitForEvent returned 0x{hr:X8}"
                });
                break;
            }
        }
    }

    public List<DebugEvent> DrainEvents(int maxCount = 50)
    {
        var events = new List<DebugEvent>();
        while (events.Count < maxCount && _eventQueue.TryDequeue(out var evt))
        {
            events.Add(evt);
        }
        return events;
    }
}

public class DebugEvent
{
    public DebugEventType Type { get; set; }
    public string Details { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public ulong? Address { get; set; }
    public uint? ProcessId { get; set; }
    public uint? ThreadId { get; set; }
}

public enum DebugEventType
{
    BreakpointHit,
    ExceptionFirstChance,
    ExceptionSecondChance,
    ModuleLoaded,
    ModuleUnloaded,
    ProcessCreated,
    ProcessExited,
    ThreadCreated,
    ThreadExited,
    BreakIn,        // Manual break via SetInterrupt
    Error
}
```

### 12.2 Important: Event Pump and DbgEng Thread Affinity

The event pump's `WaitForEvent` call AND the event callback execution both happen on the pump thread. But our MCP tool calls need to call DbgEng methods (Execute, ReadVirtual, etc.) from the same thread that created the client.

**Solution:** The event pump and the tool execution share the SAME DbgEng thread. The pump loop only runs `WaitForEvent` when no tool call is pending. We use a priority system:

```csharp
public class DbgEngThread : IDisposable
{
    private readonly BlockingCollection<WorkItem> _workQueue = new();
    private volatile bool _pumpEnabled = true;

    private void Run()
    {
        // Create DbgEng objects on this thread
        InitializeDbgEng();

        while (!_cts.IsCancellationRequested)
        {
            // Priority 1: Process any queued tool calls
            if (_workQueue.TryTake(out var work, TimeSpan.FromMilliseconds(0)))
            {
                work.Execute();
                continue;
            }

            // Priority 2: If no tool calls pending and pump enabled, wait for events
            if (_pumpEnabled && _state.KdExecStatus == DebugExecutionStatus.Go)
            {
                int hr = _control.WaitForEvent(0, 500);  // Short timeout
                if (hr == 0)
                {
                    ProcessEvent();  // Event callbacks fire here
                }
            }
            else
            {
                // Nothing to do — brief sleep to avoid busy-waiting
                Thread.Sleep(50);
            }
        }
    }
}
```

---

## 13. File Transfer Safety

### 13.1 Malware Isolation Rules

For malware analysis, file transfers are the most dangerous operation — they're the bridge between the infected VM and the host.

**Rules:**

1. **Files FROM the VM go to a quarantine directory** on the host (e.g., `C:\MCP_Quarantine\`), never to arbitrary paths.
2. **The quarantine directory should be excluded from Windows Defender** to prevent the host AV from deleting samples.
3. **Files TO the VM can come from anywhere on the host**, but the tool should log the transfer.
4. **Never execute files retrieved from the VM** on the host.
5. **Large file transfers should be chunked** and have progress reporting.

```csharp
public class SafeFileTransfer
{
    private readonly string _quarantineDir = @"C:\MCP_Quarantine";

    public async Task<TransferResult> SafeCopyFromGuest(
        GuestExecManager guest, string guestPath, string? suggestedHostFilename = null)
    {
        // Force output to quarantine directory
        var filename = suggestedHostFilename ?? Path.GetFileName(guestPath);
        var safeName = SanitizeFilename(filename);
        var hostPath = Path.Combine(_quarantineDir, DateTime.Now.ToString("yyyyMMdd_HHmmss"), safeName);

        Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);

        var result = await guest.CopyFileFromGuestAsync(guestPath, hostPath);

        if (result.Success)
        {
            // Log the transfer
            LogTransfer(guestPath, hostPath, "FROM_GUEST");

            return TransferResult.Success(
                $"File retrieved to quarantine: {hostPath} " +
                $"({new FileInfo(hostPath).Length} bytes). " +
                "WARNING: This file came from the analysis VM. " +
                "Do not execute it on the host.");
        }

        return result;
    }
}
```

---

## 14. VM Setup & Prerequisites

### 14.1 Guest VM Configuration Checklist

```
Windows Guest VM Setup:
───────────────────────

1. INSTALL WINDOWS
   □ Windows 10/11 Pro or Server 2019/2022
   □ Disable Windows Update (for stable analysis environment)
   □ Disable Windows Defender real-time protection
   □ Set administrator password (used by vmrun -gu/-gp)

2. VMWARE TOOLS
   □ Install VMware Tools (required for guest operations)
   □ Verify: vmrun checkToolsState returns "running"

3. KERNEL DEBUG — OPTION A: KDNET (Recommended for Win8+)
   □ In guest, open admin cmd:
     > bcdedit /debug on
     > bcdedit /dbgsettings net hostip:<HOST_IP> port:50000 key:1.2.3.4
   □ In VMware: configure VM network as Host-Only or NAT
   □ Reboot guest
   □ From host: verify with "kd -k net:port=50000,key=1.2.3.4"

4. KERNEL DEBUG — OPTION B: SERIAL PIPE (For Win7 or when KDNET fails)
   □ Add to .vmx file:
     serial0.present = "TRUE"
     serial0.fileType = "pipe"
     serial0.fileName = "\\.\pipe\com_1"
     serial0.pipe.endPoint = "server"
     serial0.tryNoRxLoss = "FALSE"
   □ In guest, open admin cmd:
     > bcdedit /debug on
     > bcdedit /dbgsettings serial debugport:1 baudrate:115200
   □ Reboot guest
   □ From host: verify with "kd -k com:pipe,port=\\.\pipe\com_1,resets=0,reconnect"

5. USER-MODE DEBUG TOOLS (install inside guest)
   □ frida-server.exe — copy to C:\Tools, run as admin
     Start: frida-server.exe -l 0.0.0.0:27042
   □ dbgsrv.exe — from Debugging Tools for Windows
     Start: dbgsrv.exe -t tcp:port=5064
   □ TTD.exe — from WinDbg (Preview) installation, copy TTD folder
   □ x64dbg + x64dbg-automate plugin (optional)

6. ANALYSIS TOOLS (recommended: use FLARE VM installer)
   □ Run: .\install.ps1 -password <pw> -noWait -noGui
   □ Installs: IDA Free, Ghidra, x64dbg, Process Monitor, Process Explorer,
     Wireshark, FakeNet-NG, YARA, capa, DIE, pestudio, etc.

7. SNAPSHOT
   □ Create a clean snapshot: "Clean-Ready"
   □ Create snapshot with debug tools running: "Debug-Ready"
```

### 14.2 Host Machine Prerequisites

```
Host Machine Setup:
───────────────────

1. VMware Workstation Pro (free)
   □ Install from vmware.com
   □ Verify vmrun path: "C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe"

2. Debugging Tools for Windows
   □ Install Windows SDK, select "Debugging Tools for Windows"
   □ Or install WinDbg (Preview) from Microsoft Store
   □ Verify dbgeng.dll is accessible
   □ Configure symbol path: _NT_SYMBOL_PATH=srv*C:\Symbols*https://msdl.microsoft.com/download/symbols

3. .NET 8 SDK
   □ Install from dotnet.microsoft.com
   □ Verify: dotnet --version

4. Frida tools (host-side client)
   □ pip install frida-tools
   □ Or: download frida.exe standalone

5. Quarantine directory
   □ Create C:\MCP_Quarantine
   □ Add Windows Defender exclusion for this path

6. MCP client
   □ Claude Desktop, VS Code with MCP extension, or any MCP-compatible client
   □ Configure MCP server in client's config file
```

### 14.3 MCP Server Configuration File

```json
{
  "vm": {
    "vmxPath": "C:\\VMs\\Windows11-Analysis\\Windows11-Analysis.vmx",
    "vmrunPath": "C:\\Program Files (x86)\\VMware\\VMware Workstation\\vmrun.exe",
    "guestUsername": "Admin",
    "guestPassword": "AnalysisLab123",
    "headless": true
  },
  "kernelDebug": {
    "transport": "kdnet",
    "kdnet": {
      "port": 50000,
      "key": "1.2.3.4"
    },
    "serial": {
      "pipeName": "\\\\.\\pipe\\com_1"
    },
    "symbolPath": "srv*C:\\Symbols*https://msdl.microsoft.com/download/symbols"
  },
  "guest": {
    "fridaPort": 27042,
    "dbgsrvPort": 5064,
    "x64dbgAutomatePort": 27043
  },
  "security": {
    "quarantineDir": "C:\\MCP_Quarantine",
    "maxFileTransferSizeMB": 500,
    "logDir": "C:\\MCP_Logs"
  },
  "timeouts": {
    "vmStartSeconds": 60,
    "vmStopSeconds": 30,
    "kdConnectSeconds": 30,
    "guestCommandSeconds": 60,
    "guestTransferSeconds": 120
  }
}
```

---

## 15. MCP Tool Catalog (Consolidated)

### 15.1 Design Philosophy: Fewer Tools, Smarter Validation

The LLM already knows WinDbg commands. `kd_execute("k")` gives a stack trace.
`kd_execute("r")` shows registers. `kd_execute("bp nt!NtCreateFile")` sets a
breakpoint. Having 15 wrapper tools that each do one WinDbg command adds
complexity with no benefit. **The LLM doesn't need training wheels for WinDbg.**

What the LLM CANNOT safely do through `kd_execute` is issue **execution-changing
commands** — `g`, `t`, `p`, `gu`, `wt`, `gh`, `gn`, etc. — because these cause
DbgEng to internally call `WaitForEvent()`, which blocks until the next debug
event. If no event comes (no breakpoint hit, no exception), the tool call
**hangs forever**. The MCP connection times out. The LLM is stuck. The DbgEng
thread is deadlocked.

Therefore: **execution-control commands are BLOCKED inside `kd_execute`** and
routed to dedicated tools (`kd_continue`, `kd_break`, `kd_step`) that handle
the async wait properly with timeouts.

### 15.2 Tool Registration (C# MCP SDK)

### 15.3 The Blocked Command List — Preventing kd_execute Deadlocks

These commands change execution state and MUST NOT be run via kd_execute
because they trigger `WaitForEvent()` internally and will block forever:

```csharp
private static readonly HashSet<string> BlockedCommands = new(StringComparer.OrdinalIgnoreCase)
{
    // Go variants — resume execution, block on WaitForEvent indefinitely
    "g", "gc", "gh", "gn", "gu", "gN",

    // Step variants — execute instruction(s), block on WaitForEvent
    "t", "p",           // Basic step into / step over
    "ta", "pa",         // Step/trace to address
    "tc", "pc",         // Step/trace to next call
    "tt", "pt",         // Step/trace to next return
    "th", "ph",         // Step/trace to next branch
    "wt",               // Trace and watch — can run for VERY long time

    // Session destroyers — break MCP server state
    "q", "qq",          // Quit debugger — kills DbgEng session
    ".detach",          // Detach — use kd_disconnect tool instead
    ".restart",         // Restart target — unpredictable state change
    ".reboot",          // Reboot target — everything breaks
};

/// <summary>
/// Checks if a command would deadlock or corrupt state.
/// Handles compound commands ("bp foo; g") and commands with args ("g @$ra").
/// </summary>
private static (bool IsBlocked, string BlockedCmd, string Suggestion) CheckCommand(string command)
{
    var parts = command.Split(';', StringSplitOptions.TrimEntries);

    foreach (var part in parts)
    {
        var baseCmd = part.Split(' ', 2, StringSplitOptions.TrimEntries)[0];

        if (BlockedCommands.Contains(baseCmd))
        {
            string suggestion = baseCmd switch
            {
                "g" or "gc" or "gh" or "gn" or "gN"
                    => "Use kd_continue instead (which handles the async wait safely).",
                "gu"
                    => "Use kd_continue instead. To run until return, set a breakpoint "
                     + "on the return address first: kd_execute('bp @$ra'), then kd_continue.",
                "t" or "ta" or "tc" or "tt" or "th"
                    => "Use kd_step(mode='into') instead (which has a timeout).",
                "p" or "pa" or "pc" or "pt" or "ph"
                    => "Use kd_step(mode='over') instead (which has a timeout).",
                "wt"
                    => "wt can run for minutes. Use kd_step(mode='over') for single steps, "
                     + "or set a breakpoint and kd_continue instead.",
                "q" or "qq"
                    => "Use kd_disconnect to safely end the debug session.",
                ".detach"
                    => "Use kd_disconnect to safely detach.",
                ".restart" or ".reboot"
                    => "Use vm_stop + vm_start to restart, or vm_snapshot_restore to revert.",
                _ => "Use the appropriate dedicated tool instead."
            };

            return (true, baseCmd, suggestion);
        }
    }

    return (false, "", "");
}
```

### 15.4 kd_execute Implementation with Blocked Command Gate

```csharp
public async Task<string> kd_execute(string command, int timeoutSeconds = 30)
{
    // ── GATE 1: State precondition (target must be broken in) ──
    var precheck = await _stateCoordinator.ValidatePreconditionsAsync("kd_execute");
    if (precheck != null) return precheck.ErrorMessage;

    // ── GATE 2: Blocked command detection ──
    var (isBlocked, blockedCmd, suggestion) = CheckCommand(command);
    if (isBlocked)
    {
        return $"⚠️ BLOCKED: '{blockedCmd}' is an execution-control command that would "
             + "cause this tool to hang indefinitely waiting for a debug event. "
             + suggestion;
    }

    // ── GATE 3: Execute with timeout ──
    return await ExecuteWithTimeoutAsync("kd_execute", async (ct) =>
    {
        return await _dbgEng.ExecuteCommandAsync(command, TimeSpan.FromSeconds(timeoutSeconds));
    }, TimeSpan.FromSeconds(timeoutSeconds + 5)); // outer timeout slightly larger
}
```

### 15.5 BSOD / Bugcheck Detection in State

When a BSOD happens, the kernel debugger receives a bugcheck exception and
breaks in. We need to detect this and provide specific guidance, because
a BSOD break is different from a normal breakpoint — the OS is dead, not
just paused. Continuing won't help.

```csharp
public class SystemState
{
    // ... existing fields ...

    // BSOD detection
    public bool IsBugcheck { get; set; }             // Is the break due to a BSOD?
    public string? BugcheckCode { get; set; }        // e.g., "0x0000007E SYSTEM_THREAD_EXCEPTION_NOT_HANDLED"
}

// In RefreshStateAsync, after detecting Break state:
private async Task DetectBugcheck()
{
    if (_state.KdExecStatus != DebugExecutionStatus.Break)
    {
        _state.IsBugcheck = false;
        _state.BugcheckCode = null;
        return;
    }

    // Method 1: Check .lastevent output
    var result = await _dbgEng.ExecuteCommandAsync(".lastevent", TimeSpan.FromSeconds(5));
    if (result.Success)
    {
        // .lastevent output for bugcheck contains "Bugcheck" or "Bug Check"
        if (result.Output.Contains("bugcheck", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("Bug Check", StringComparison.OrdinalIgnoreCase))
        {
            _state.IsBugcheck = true;

            // Extract bugcheck code from output
            // Typical: "Last event: ... Bugcheck 7E..."
            var match = Regex.Match(result.Output, @"[Bb]ug\s*[Cc]heck\s+([\dA-Fa-f]+)");
            if (match.Success)
                _state.BugcheckCode = $"0x{match.Groups[1].Value}";

            return;
        }
    }

    // Method 2: Try reading KiBugCheckData
    // If we can read the bugcheck code from the known kernel variable:
    var bcResult = await _dbgEng.ExecuteCommandAsync("dd KiBugCheckData L1", TimeSpan.FromSeconds(3));
    if (bcResult.Success && !bcResult.Output.Contains("error", StringComparison.OrdinalIgnoreCase))
    {
        // If KiBugCheckData is nonzero, we're in a bugcheck
        // This is a secondary check
    }

    _state.IsBugcheck = false;
    _state.BugcheckCode = null;
}
```

### 15.6 BSOD-Aware Error Messages

The precondition checks now account for BSOD state:

```csharp
private ToolResult? RequireGuestOpsAvailable()
{
    // ... existing VM power checks ...

    if (_state.KdConnected && _state.KdExecStatus == DebugExecutionStatus.Break)
    {
        if (_state.IsBugcheck)
        {
            return ToolResult.Error(
                $"🔵 BSOD DETECTED — Bugcheck {_state.BugcheckCode ?? "unknown"}. "
                + "The guest OS has crashed. Guest operations will NOT work. "
                + "You can: (1) kd_execute('!analyze -v') to analyze the crash, "
                + "(2) vm_snapshot_restore to revert to a clean state, or "
                + "(3) vm_stop + vm_start to reboot (crash dump may be generated).");
        }

        return ToolResult.Error(
            "VM is frozen — kernel debugger is at a breakpoint. "
            + "Guest operations require the OS to be running. "
            + "Call kd_continue to resume the target, then retry.");
    }

    // ... existing Tools check ...
    return null;
}

private ToolResult? RequireKdConnected_TargetRunning()
{
    if (!_state.KdConnected)
        return ToolResult.Error("Kernel debugger is not connected. Call kd_connect first.");

    if (_state.KdExecStatus == DebugExecutionStatus.Break)
    {
        if (_state.IsBugcheck)
        {
            return ToolResult.Error(
                $"🔵 BSOD — cannot resume execution, the OS has crashed "
                + $"(Bugcheck {_state.BugcheckCode ?? "unknown"}). "
                + "Use kd_execute('!analyze -v') to investigate, then "
                + "vm_snapshot_restore to recover.");
        }

        return ToolResult.Error(
            "Target is already halted. You can inspect state with kd_execute, "
            + "or call kd_continue to resume execution.");
    }

    return null;
}

// kd_continue also checks for BSOD:
private ToolResult? RequireKdConnected_TargetBroken_CanResume()
{
    var baseCheck = RequireKdConnected_TargetBroken();
    if (baseCheck != null) return baseCheck;

    if (_state.IsBugcheck)
    {
        return ToolResult.Error(
            $"🔵 BSOD — Bugcheck {_state.BugcheckCode ?? "unknown"}. "
            + "The OS has crashed and cannot be resumed. Options: "
            + "(1) kd_execute('!analyze -v') to analyze the crash. "
            + "(2) vm_snapshot_restore('Clean-Ready') to revert to a clean state. "
            + "(3) vm_stop(hard=true) then vm_start to reboot.");
    }

    return null;
}
```

### 15.7 get_system_state Output with BSOD Info

```csharp
public async Task<string> get_system_state()
{
    await _stateCoordinator.RefreshStateAsync();
    var s = _stateCoordinator.State;

    var sb = new StringBuilder();
    sb.AppendLine("=== SYSTEM STATE ===");
    sb.AppendLine();

    // VM
    sb.AppendLine($"VM Power:          {s.VmPower}");
    sb.AppendLine($"VMware Tools:      {s.VmTools}");
    sb.AppendLine($"VM IP Address:     {s.VmIpAddress ?? "unknown"}");
    sb.AppendLine();

    // Kernel Debugger
    sb.AppendLine($"KD Connected:      {s.KdConnected}");
    if (s.KdConnected)
    {
        sb.AppendLine($"KD Transport:      {s.KdTransportType}");
        sb.AppendLine($"Execution Status:  {s.KdExecStatus}");

        if (s.KdExecStatus == DebugExecutionStatus.Break)
        {
            sb.AppendLine($"Break Reason:      {s.KdBreakReason ?? "unknown"}");

            if (s.IsBugcheck)
            {
                sb.AppendLine($"🔵 BSOD DETECTED:  {s.BugcheckCode}");
                sb.AppendLine($"   The OS has CRASHED. Guest ops will NOT work.");
                sb.AppendLine($"   Run kd_execute('!analyze -v') or vm_snapshot_restore.");
            }
        }

        sb.AppendLine($"Pending Events:    {s.PendingEventCount}");
        sb.AppendLine($"Wait Pending:      {s.KdWaitPending}");
    }
    sb.AppendLine();

    // Guest operations
    sb.AppendLine($"Guest Ops Available: {s.GuestOpsAvailable}");
    if (!s.GuestOpsAvailable)
    {
        // Explain WHY guest ops are unavailable
        if (s.VmPower != VmPowerState.Running)
            sb.AppendLine($"   → VM is {s.VmPower}");
        else if (s.KdConnected && s.KdExecStatus == DebugExecutionStatus.Break)
            sb.AppendLine($"   → Kernel debugger has frozen the VM (call kd_continue)");
        else if (s.VmTools != VmToolsState.Running)
            sb.AppendLine($"   → VMware Tools: {s.VmTools}");
    }
    sb.AppendLine();

    // User-mode debug
    if (s.FridaState != null)
        sb.AppendLine($"Frida:             {s.FridaState}");
    if (s.DbgsrvState != null)
        sb.AppendLine($"dbgsrv:            {s.DbgsrvState}");

    return sb.ToString();
}
```

### 15.8 Complete Tool List

```csharp
// ═══════════════════════════════════════════════════════════════
//  VM TOOLS — 8 tools
// ═══════════════════════════════════════════════════════════════
//  vm_start, vm_stop, vm_pause, vm_resume
//  vm_snapshot_create, vm_snapshot_restore, vm_snapshot_list
//  vm_screenshot

// ═══════════════════════════════════════════════════════════════
//  KERNEL DEBUG TOOLS — 6 tools (down from 16!)
//  kd_execute does EVERYTHING except execution control.
//  Execution-changing commands are BLOCKED in kd_execute.
// ═══════════════════════════════════════════════════════════════
//  kd_connect          — attach to kernel debugger
//  kd_disconnect       — detach from kernel debugger
//  kd_break            — halt target (requires: target running)
//  kd_continue         — resume target (requires: target broken, NOT bsod)
//  kd_step             — step one instruction (mode=into|over, has timeout)
//  kd_execute          — run ANY WinDbg command except execution-changers
//  kd_wait_for_event   — poll for breakpoint hits / exceptions

// ═══════════════════════════════════════════════════════════════
//  GUEST TOOLS — 5 tools
// ═══════════════════════════════════════════════════════════════
//  guest_run_command, guest_transfer_to_vm, guest_transfer_from_vm
//  guest_list_processes, guest_kill_process

// ═══════════════════════════════════════════════════════════════
//  USER-MODE DEBUG TOOLS — 6 tools
// ═══════════════════════════════════════════════════════════════
//  umd_frida_attach, umd_frida (multi-action)
//  umd_dbgsrv_connect, umd_dbgsrv_execute
//  umd_ttd, umd_ttd_query

// ═══════════════════════════════════════════════════════════════
//  META — 1 tool
// ═══════════════════════════════════════════════════════════════
//  get_system_state

// TOTAL: 27 tools (down from 40+)
```
```

---

## 16. Project Structure & Implementation Order

### 16.1 Solution Structure

```
WinDbgMCP/
├── WinDbgMCP.sln
├── src/
│   ├── WinDbgMCP.Server/                    # Main MCP server project
│   │   ├── Program.cs                        # Entry point, MCP server setup
│   │   ├── Configuration/
│   │   │   ├── ServerConfig.cs               # Configuration model
│   │   │   └── appsettings.json              # Default configuration
│   │   ├── State/
│   │   │   ├── SystemState.cs                # State model
│   │   │   ├── StateCoordinator.cs           # Precondition gate + state management
│   │   │   └── ErrorMessages.cs              # LLM-friendly error messages
│   │   ├── Vmware/
│   │   │   ├── VmwareManager.cs              # vmrun wrapper
│   │   │   └── VmrunProcess.cs               # Process execution helper
│   │   ├── KernelDebug/
│   │   │   ├── DbgEngThread.cs               # Thread affinity manager
│   │   │   ├── DbgEngManager.cs              # DbgEng operations
│   │   │   ├── EventPump.cs                  # Event loop
│   │   │   ├── OutputCapture.cs              # Output callbacks
│   │   │   ├── Interop/
│   │   │   │   ├── IDebugClient.cs           # COM interface declarations
│   │   │   │   ├── IDebugControl.cs
│   │   │   │   ├── IDebugDataSpaces.cs
│   │   │   │   ├── IDebugSymbols.cs
│   │   │   │   ├── IDebugRegisters.cs
│   │   │   │   ├── IDebugEventCallbacks.cs
│   │   │   │   ├── IDebugOutputCallbacks.cs
│   │   │   │   └── Constants.cs              # DEBUG_STATUS_*, HR values
│   │   │   └── Models/
│   │   │       ├── BreakpointInfo.cs
│   │   │       ├── DebugEvent.cs
│   │   │       ├── StackFrame.cs
│   │   │       └── ModuleInfo.cs
│   │   ├── GuestExec/
│   │   │   ├── GuestExecManager.cs           # Guest command execution
│   │   │   └── SafeFileTransfer.cs           # Quarantined file transfer
│   │   ├── UserModeDebug/
│   │   │   ├── FridaManager.cs               # Frida integration
│   │   │   ├── DbgsrvManager.cs              # dbgsrv remote debugging
│   │   │   ├── X64DbgManager.cs              # x64dbg Automate
│   │   │   └── TtdManager.cs                 # Time Travel Debugging
│   │   └── Tools/                            # MCP tool definitions
│   │       ├── VmTools.cs
│   │       ├── KernelDebugTools.cs
│   │       ├── GuestTools.cs
│   │       ├── UserModeDebugTools.cs
│   │       └── MetaTools.cs
│   │
│   └── WinDbgMCP.Tests/                      # Test project
│       ├── State/
│       │   ├── StateCoordinatorTests.cs
│       │   └── PreconditionTests.cs
│       ├── Vmware/
│       │   └── VmwareManagerTests.cs
│       ├── KernelDebug/
│       │   └── DbgEngManagerTests.cs
│       └── Integration/
│           └── EndToEndTests.cs
│
├── scripts/
│   ├── setup-guest.ps1                       # Automated guest VM setup
│   ├── install-frida-server.ps1              # Install frida-server in guest
│   └── configure-kdnet.ps1                   # Configure KDNET in guest
│
├── docs/
│   ├── SETUP.md                              # Setup instructions
│   ├── ARCHITECTURE.md                       # This document
│   └── TROUBLESHOOTING.md                    # Common issues and fixes
│
└── README.md
```

### 16.2 Implementation Phases

```
PHASE 1: Foundation (Week 1-2)
══════════════════════════════
Priority: Get the skeleton running with basic VM control

□ 1.1  Project scaffolding (dotnet new, NuGet packages)
       - ModelContextProtocol SDK
       - System.Runtime.InteropServices (for COM)
       - Microsoft.Extensions.Configuration
       - Microsoft.Extensions.Logging

□ 1.2  Configuration system (appsettings.json, ServerConfig.cs)

□ 1.3  SystemState model + StateCoordinator (empty precondition checks)

□ 1.4  VmwareManager — implement all vmrun operations
       - Start, stop, pause, resume
       - Snapshot create/restore/list
       - GetPowerState, AreToolsRunning, GetGuestIPAddress
       - Screenshot

□ 1.5  MCP server entry point + VmTools registration

□ 1.6  get_system_state tool (returns VM state only for now)

□ 1.7  TEST: Can the LLM start/stop/snapshot a VM?


PHASE 2: Kernel Debugging (Week 3-5)
═════════════════════════════════════
Priority: This is the hardest part. Get DbgEng working first.

□ 2.1  COM interface declarations (IDebugClient, IDebugControl, etc.)
       - Consider using ClrDebug NuGet for pre-built interfaces
       - If manual: get vtable order right by testing each method

□ 2.2  DbgEngThread — dedicated thread with work queue

□ 2.3  DbgEngManager.ConnectKernelAsync — KDNET and serial
       - Test with manual kd.exe first to verify guest config
       - Then implement programmatic attach

□ 2.4  OutputCapture (IDebugOutputCallbacks)

□ 2.5  GetExecutionStatus — verify it returns correct values

□ 2.6  ExecuteCommandAsync — run WinDbg commands, capture output

□ 2.7  Execution control: Break, Continue, StepInto, StepOver

□ 2.8  Memory read/write

□ 2.9  Breakpoint management

□ 2.10 Info queries: stack trace, registers, modules

□ 2.11 Precondition gate: wire up all kd_* tool preconditions

□ 2.12 EventPump — basic version with event queue

□ 2.13 kd_wait_for_event and kd_get_events

□ 2.14 TEST: Can the LLM connect, break, read memory, set breakpoints,
       continue, and detect breakpoint hits?


PHASE 3: Guest Execution (Week 6)
══════════════════════════════════
Priority: File transfer and command execution with frozen-VM protection

□ 3.1  GuestExecManager — RunCommandAsync with stdout/stderr capture

□ 3.2  File transfer (to/from guest) with quarantine

□ 3.3  Process listing and killing

□ 3.4  Precondition gate: wire up frozen-VM check for all guest tools

□ 3.5  TEST: Can the LLM execute commands, transfer files, and get
       proper errors when VM is kernel-frozen?


PHASE 4: User-Mode Debugging (Week 7-8)
════════════════════════════════════════
Priority: Frida first (most useful), then dbgsrv, then TTD

□ 4.1  FridaManager — CLI wrapper approach
       - Attach by PID/name
       - Inject script
       - Hook function helper
       - Trace calls

□ 4.2  DbgsrvManager — remote user-mode via DbgEng
       - ConnectProcessServer
       - AttachProcess
       - Same Execute/ReadMemory/etc. as kernel debug

□ 4.3  TtdManager — recording via guest command, query via host DbgEng

□ 4.4  X64DbgManager — x64dbg Automate TCP protocol (optional, lower priority)

□ 4.5  Precondition gate: wire up user-mode debug tool preconditions

□ 4.6  TEST: Can the LLM attach Frida, hook API calls, record TTD?


PHASE 5: Polish & Edge Cases (Week 9-10)
═════════════════════════════════════════
Priority: Make it robust for real-world use

□ 5.1  Snapshot restore state reset (invalidate all sessions)
□ 5.2  Connection loss detection and recovery
□ 5.3  Comprehensive logging
□ 5.4  Error message review (are they all LLM-actionable?)
□ 5.5  Guest VM setup automation script
□ 5.6  Documentation: SETUP.md, TROUBLESHOOTING.md
□ 5.7  End-to-end integration tests
□ 5.8  Performance profiling (is state refresh fast enough?)
```

---

## 17. Edge Cases & Failure Modes

### 17.1 Identified Edge Cases and Their Handling

```
EDGE CASE                              │ HANDLING
───────────────────────────────────────┼──────────────────────────────────────────
LLM calls kd_execute("g")             │ BLOCKED by CheckCommand(). Returns error:
                                       │ "Blocked: 'g' is an execution-control cmd
                                       │ that would hang forever. Use kd_continue."
                                       │ Same for t, p, gu, wt, q, .detach, etc.
───────────────────────────────────────┼──────────────────────────────────────────
LLM calls kd_execute("bp foo; g")     │ BLOCKED — CheckCommand splits on ';' and
                                       │ detects 'g' in the compound command.
                                       │ Error tells LLM to split into two calls:
                                       │ kd_execute("bp foo") then kd_continue.
───────────────────────────────────────┼──────────────────────────────────────────
LLM calls kd_break while already      │ Precondition: RequireKdConnected_TargetRunning
broken in                              │ checks KdExecStatus != Break. Returns:
                                       │ "Target is already halted. You can inspect
                                       │ state with kd_execute or kd_continue."
───────────────────────────────────────┼──────────────────────────────────────────
LLM calls kd_continue while target    │ Precondition: RequireKdConnected_TargetBroken
is already running                     │ _CanResume checks KdExecStatus == Break.
                                       │ Returns: "Target is already running."
───────────────────────────────────────┼──────────────────────────────────────────
VM BSoDs (blue screen crash)           │ Kernel debugger receives bugcheck exception.
                                       │ State → Break + IsBugcheck=true.
                                       │ get_system_state shows "🔵 BSOD DETECTED".
                                       │ kd_continue BLOCKED: "OS crashed, can't resume"
                                       │ guest_* BLOCKED: "OS is dead, not just paused"
                                       │ kd_execute WORKS: "!analyze -v" to investigate.
                                       │ vm_snapshot_restore: recommended recovery path.
───────────────────────────────────────┼──────────────────────────────────────────
BSOD + LLM tries guest_run_command    │ BSOD-specific error: "OS has CRASHED, not just
                                       │ paused. Guest ops impossible. Use !analyze -v
                                       │ or vm_snapshot_restore."
───────────────────────────────────────┼──────────────────────────────────────────
BSOD + LLM tries umd_frida_attach    │ Same: RequireGuestOpsAvailable detects BSOD,
                                       │ returns BSOD-specific error with recovery steps.
───────────────────────────────────────┼──────────────────────────────────────────
VM reboots during kernel debug session │ DbgEng detects target disconnect.
                                       │ Event pump gets error. State → NoDebuggee.
                                       │ Auto-update state. LLM must kd_connect again.
───────────────────────────────────────┼──────────────────────────────────────────
VM BSoDs (blue screen crash)           │ Kernel debugger receives bugcheck exception.
                                       │ State → Break (at bugcheck handler).
                                       │ LLM can inspect crash via !analyze -v.
                                       │ Guest ops won't work (OS is dead).
───────────────────────────────────────┼──────────────────────────────────────────
vmrun hangs (Tools not responding)     │ Every vmrun call has a timeout.
                                       │ On timeout: kill vmrun process, return error.
                                       │ Common cause: kernel debugger broke in.
───────────────────────────────────────┼──────────────────────────────────────────
DbgEng DLL not found                   │ Check on startup. Clear error:
                                       │ "Install Debugging Tools for Windows."
───────────────────────────────────────┼──────────────────────────────────────────
Symbol download slow/fails             │ First kd_execute after connect may be slow
                                       │ (symbol download). Use timeout of 60s for
                                       │ first command. Cache symbols locally.
───────────────────────────────────────┼──────────────────────────────────────────
LLM calls kd_step in rapid succession  │ Each step queues on DbgEngThread. Sequential
                                       │ execution prevents reentrancy. Each step
                                       │ completes before next starts.
───────────────────────────────────────┼──────────────────────────────────────────
LLM calls guest_run_command with       │ guestCommandTimeout limits execution.
long-running process (infinite loop)   │ On timeout: vmrun process killed, guest
                                       │ process may still be running. Return error
                                       │ suggesting guest_kill_process.
───────────────────────────────────────┼──────────────────────────────────────────
frida-server not running in guest      │ Frida attach fails with connection refused.
                                       │ Error message: "frida-server not detected.
                                       │ Run guest_run_command to start it:
                                       │ C:\Tools\frida-server.exe -l 0.0.0.0:27042"
───────────────────────────────────────┼──────────────────────────────────────────
Network isolation blocks KDNET         │ KDNET requires host↔guest connectivity.
                                       │ If using Host-Only with no host adapter,
                                       │ KDNET won't work. Fall back to serial pipe.
                                       │ Config should specify both options.
───────────────────────────────────────┼──────────────────────────────────────────
Guest password changed/wrong           │ vmrun returns error "Authentication failed".
                                       │ Error: "Guest authentication failed. Verify
                                       │ guestUsername and guestPassword in config."
───────────────────────────────────────┼──────────────────────────────────────────
Two debugger clients compete           │ Only one kernel debugger can attach.
                                       │ If WinDbg GUI is open, MCP can't connect.
                                       │ Error: "Connection refused — another debugger
                                       │ may be attached. Close WinDbg and retry."
───────────────────────────────────────┼──────────────────────────────────────────
Snapshot restore while kd_step pending │ HandleSnapshotRestore forcefully disconnects
                                       │ DbgEng, cancels pending wait, resets state.
                                       │ LLM gets: "All sessions invalidated."
───────────────────────────────────────┼──────────────────────────────────────────
MCP server crashes/restarts            │ All state is lost (in-memory only).
                                       │ On restart: probe VM state, detect if VM is
                                       │ running. Don't auto-reconnect debugger.
                                       │ LLM calls get_system_state to discover.
───────────────────────────────────────┼──────────────────────────────────────────
Concurrent MCP tool calls              │ SemaphoreSlim in StateCoordinator serializes
                                       │ precondition checks. DbgEngThread serializes
                                       │ all DbgEng calls. vmrun calls CAN be
                                       │ concurrent (different processes) but should
                                       │ be serialized for safety.
```

---

## 18. Testing Strategy

### 18.1 Unit Tests (No VM Required)

```
□ StateCoordinator precondition logic
  - Every state combination × every tool = correct allow/deny
  - Error messages contain actionable instructions
  - State refresh updates derived states correctly

□ VmrunProcess argument building and parsing
  - Correct quoting of paths with spaces
  - Exit code interpretation
  - Timeout behavior

□ OutputCapture buffer management
  - Thread-safe append and drain
  - No data loss under concurrent output

□ ErrorMessages formatting
  - All messages follow [WHAT]—[WHY]—[WHAT TO DO] template
```

### 18.2 Integration Tests (VM Required)

```
□ VM lifecycle: start → verify running → snapshot → stop → restore → verify running
□ Kernel debug: connect → break → read memory → set BP → continue → BP hit → disconnect
□ Guest exec: run command → capture output → transfer file → verify content
□ Frozen VM: connect KD → break → try guest command → verify error → continue → retry guest command → success
□ Snapshot reset: connect KD + Frida → snapshot restore → verify all sessions dead → reconnect
□ Timeout: start long guest command → verify timeout fires → verify VM still functional
```

---

## 19. Future Extensions

```
v2 Roadmap:
───────────
□ Multi-VM support (analyzer VM + target VM)
□ IDA Pro / Ghidra MCP bridge (static analysis integration)
□ Automated malware detonation pipeline
□ Network capture integration (Wireshark/tshark via guest command)
□ Memory dump acquisition and analysis
□ Driver loading automation
□ Hypervisor-level introspection (VMware VProbes or custom hypervisor)
□ Persistent session state (survive MCP server restart)
□ Web UI dashboard showing VM state + debug state
□ Multi-client support (multiple LLM agents cooperating)
```

---

## Summary of Critical Design Decisions

1. **Every tool calls `GetExecutionStatus()` via the state coordinator** before executing. No exceptions. This is the #1 defense against deadlocks and state corruption.

2. **Every operation has a timeout.** No blocking calls anywhere in the system. If something hangs, it times out and returns an error that tells the LLM what to do.

3. **Error messages are LLM prompts.** Every error follows `[WHAT]—[WHY]—[WHAT TO DO]` and explicitly names the next tool to call. The LLM self-corrects instead of getting stuck.

4. **Execution-control commands are BLOCKED inside kd_execute.** Commands like `g`, `t`, `p`, `gu`, `wt` would cause `WaitForEvent` to block indefinitely. They are detected by `CheckCommand()` and rejected with an error pointing to `kd_continue` or `kd_step`. Compound commands (`bp foo; g`) are also caught. This is the #2 defense against deadlocks.

5. **BSOD is detected and handled differently from normal breakpoints.** A BSOD sets `IsBugcheck=true` in state. This changes error messages across the entire system: `kd_continue` is blocked ("OS is dead, can't resume"), `guest_*` tools get "OS has CRASHED" instead of "call kd_continue", and `get_system_state` prominently displays the bugcheck code. Only `kd_execute` and `vm_snapshot_restore` remain useful.

6. **Consolidated tool set (27 tools, not 40+).** `kd_execute` handles all WinDbg commands (read memory, registers, breakpoints, modules, stack traces, etc.). Only execution control (`kd_break`, `kd_continue`, `kd_step`) and event waiting (`kd_wait_for_event`) are separate tools, because they have fundamentally different async/timeout semantics.

7. **DbgEng thread affinity is non-negotiable.** All COM calls go through a single dedicated thread. The event pump shares this thread with tool execution using a priority queue.

8. **Snapshot restore is a nuclear reset.** It destroys all state — kernel debug, Frida, dbgsrv, everything. The state coordinator handles this explicitly.

9. **Guest operations are gated behind a compound check:** VM running AND Tools responsive AND kernel debugger NOT in break state AND NOT in BSOD. All conditions must be met.

10. **The `get_system_state` tool is the LLM's ground truth.** It's always available, always works, and returns everything the LLM needs to plan its next action — including whether the current break is a normal breakpoint or a BSOD.
