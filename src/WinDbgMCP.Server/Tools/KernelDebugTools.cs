using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.KernelDebug;
using WinDbgMCP.Server.KernelDebug.Interop;
using WinDbgMCP.Server.State;

namespace WinDbgMCP.Server.Tools;

[McpServerToolType]
public static class KernelDebugTools
{
    [McpServerTool(Name = "kd_connect"), Description(
        "Connect to the kernel debug target via KDNET or serial. " +
        "The VM must be running with debug boot enabled. " +
        "Target will break on connect (initial breakpoint). " +
        "Optionally provide a raw connection string (e.g. 'net:port=50000,key=...' or 'com:pipe,port=\\\\.\\pipe\\com_1,resets=0,reconnect'); " +
        "if omitted, connects using the defaults from appsettings.json.")]
    public static async Task<string> KdConnect(
        StateCoordinator state,
        DbgEngManager dbgEng,
        ServerConfig config,
        [Description("Optional raw DbgEng connection string. If omitted, uses appsettings.json defaults.")] string? connectionString = null,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_connect");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var result = await dbgEng.ConnectKernelAsync(connectionString, ct);

            // Update state coordinator with actual transport type
            KdTransport transport;
            if (connectionString != null)
                transport = connectionString.StartsWith("net:", StringComparison.OrdinalIgnoreCase) ? KdTransport.KDNET : KdTransport.Serial;
            else
                transport = config.KernelDebug.Transport.Equals("kdnet", StringComparison.OrdinalIgnoreCase) ? KdTransport.KDNET : KdTransport.Serial;
            state.SetKdConnected(transport);

            return result;
        }
        catch (OperationCanceledException)
        {
            var totalTimeout = DbgEngManager.GetConnectOperationTimeout(config.Timeouts).TotalSeconds;
            return "kd_connect timed out. The kernel debug target did not complete attach/initial-break within " +
                   $"{totalTimeout:0}s " +
                   $"(attach budget {config.Timeouts.KdConnectSeconds}s, initial-break budget " +
                   $"{config.Timeouts.KdInitialBreakSeconds}s). Verify: " +
                   "(1) VM is running with debug boot enabled (bcdedit /debug on + KDNET configured). " +
                   "(2) The KDNET port/key matches appsettings.json. " +
                   "(3) No other debugger is already attached. " +
                   "(4) Host firewall allows UDP port inbound.";
        }
        catch (Exception ex)
        {
            return $"kd_connect failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_disconnect"), Description(
        "Disconnect from the kernel debug target. " +
        "Resumes the target before disconnecting so the VM keeps running.")]
    public static async Task<string> KdDisconnect(
        StateCoordinator state,
        DbgEngManager dbgEng,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_disconnect");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var result = await dbgEng.DisconnectAsync();
            state.SetKdDisconnected();
            return result;
        }
        catch (Exception ex)
        {
            // Even if disconnect throws, mark as disconnected
            state.SetKdDisconnected();
            return $"kd_disconnect completed with error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_break"), Description(
        "Break into a running target (equivalent to Ctrl+Break in WinDbg). " +
        "Target must be running. After breaking, use kd_execute to inspect state.")]
    public static async Task<string> KdBreak(
        StateCoordinator state,
        DbgEngManager dbgEng,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_break");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var result = await dbgEng.BreakAsync();
            state.SetKdBroken("Manual break");

            // Check for BSOD after break
            var (isBugcheck, bugcheckCode) = await dbgEng.DetectBugcheckAsync();
            if (isBugcheck)
            {
                state.SetBsodDetected(bugcheckCode);
                return result + $"\n\nWARNING: BSOD DETECTED (bugcheck {bugcheckCode}). " +
                       "The OS has crashed. Use kd_execute('!analyze -v') to investigate. " +
                       "Guest operations will NOT work. Use vm_snapshot_restore to recover.";
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return "kd_break timed out. The target may not be in a state where it can break.";
        }
        catch (Exception ex)
        {
            return $"kd_break failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_continue"), Description(
        "Resume target execution (go). Returns immediately — the target starts running. " +
        "Use kd_wait_for_event to check for breakpoint hits, or kd_break to halt again. " +
        "Guest operations (vm_execute, vm_send_file) require the target to be running.")]
    public static async Task<string> KdContinue(
        StateCoordinator state,
        DbgEngManager dbgEng,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_continue");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var result = await dbgEng.ContinueAsync();
            state.SetKdRunning();
            return result;
        }
        catch (OperationCanceledException)
        {
            return "kd_continue timed out.";
        }
        catch (Exception ex)
        {
            return $"kd_continue failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_step"), Description(
        "Step one instruction. Mode 'over' steps over calls, 'into' steps into calls. " +
        "Target must be at a breakpoint (broken). Returns the new instruction pointer and disassembly.")]
    public static async Task<string> KdStep(
        StateCoordinator state,
        DbgEngManager dbgEng,
        [Description("Step mode: 'into' (step into calls) or 'over' (step over calls, default)")] string mode = "over",
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_step");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            return await dbgEng.StepAsync(mode);
        }
        catch (OperationCanceledException)
        {
            return "kd_step timed out. The target may be in an unexpected state.";
        }
        catch (Exception ex)
        {
            return $"kd_step failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_execute"), Description(
        "Execute any WinDbg command and return output. Target must be halted (at breakpoint). " +
        "Examples: 'k' (stack), 'r' (registers), 'lm' (modules), '!process 0 0', '!analyze -v', " +
        "'db addr' (memory), 'u addr' (disassemble), 'bp symbol' (set breakpoint). " +
        "Execution-control commands (g, t, p, gu, wt) are BLOCKED — use kd_continue/kd_step instead.")]
    public static async Task<string> KdExecute(
        StateCoordinator state,
        DbgEngManager dbgEng,
        ServerConfig config,
        [Description("WinDbg command to execute")] string command,
        [Description("Timeout in seconds (default 30)")] int timeoutSeconds = 30,
        [Description("If true, save full command output to KernelDebug.TranscriptDirectory and return the path.")] bool saveTranscript = false,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_execute");
        if (precheck != null) return precheck.ErrorMessage!;

        // Check for blocked commands
        var (isBlocked, blockedCmd, suggestion) = DbgEngConstants.CheckCommand(command);
        if (isBlocked)
        {
            return $"BLOCKED: The command '{blockedCmd}' changes execution state and " +
                   $"would hang the debugger if run via kd_execute. {suggestion}";
        }

        try
        {
            var output = await dbgEng.ExecuteCommandAsync(command, timeoutSeconds);
            if (!saveTranscript)
            {
                return output;
            }

            var path = SaveTranscript(config, command, output);
            return $"TranscriptPath: {path}\n\n{output}";
        }
        catch (OperationCanceledException)
        {
            return $"Command '{command}' timed out after {timeoutSeconds}s. " +
                   "The command may be waiting for something. Try kd_break to interrupt.";
        }
        catch (Exception ex)
        {
            return $"kd_execute failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_symbol_status"), Description(
        "Report kernel symbol health. Use this when commands such as !process fail with symbol errors.")]
    public static async Task<string> KdSymbolStatus(
        StateCoordinator state,
        DbgEngManager dbgEng,

        [Description("Timeout in seconds (default 30)")] int timeoutSeconds = 30,

        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_execute");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var symbolPath = await dbgEng.ExecuteCommandAsync(".sympath", timeoutSeconds);
            var ntModules = await dbgEng.ExecuteCommandAsync("lm m nt", timeoutSeconds);
            var ntVerbose = await dbgEng.ExecuteCommandAsync("lm vm nt", timeoutSeconds);
            var exportOnly = ntModules.Contains("export symbols", StringComparison.OrdinalIgnoreCase) ||
                             ntVerbose.Contains("export symbols", StringComparison.OrdinalIgnoreCase);
            var pdbSymbols = ntModules.Contains("pdb symbols", StringComparison.OrdinalIgnoreCase) ||
                             ntVerbose.Contains("pdb symbols", StringComparison.OrdinalIgnoreCase);
            var symbolErrors = LooksLikeSymbolProblem(ntModules) || LooksLikeSymbolProblem(ntVerbose);

            var status = pdbSymbols && !exportOnly && !symbolErrors
                ? "OK: nt appears to have PDB symbols loaded."
                : "WARN: nt symbols may be incomplete or export-only.";

            return $"""
=== KD SYMBOL STATUS ===
{status}

Recommended reload if this is WARN:
  .symfix
  .reload /f nt
  lm vm nt

--- .sympath ---
{symbolPath}

--- lm m nt ---
{ntModules}

--- lm vm nt ---
{ntVerbose}
""";
        }
        catch (OperationCanceledException)
        {
            return $"kd_symbol_status timed out after {timeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return $"kd_symbol_status failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_find_process_by_name"), Description(
        "Find a kernel process by image name using !process. Returns the EPROCESS address if symbols support the query.")]
    public static async Task<string> KdFindProcessByName(
        StateCoordinator state,
        DbgEngManager dbgEng,

        [Description("Image name, e.g. lsass.exe")] string name,

        [Description("Timeout in seconds (default 30)")] int timeoutSeconds = 30,

        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_execute");
        if (precheck != null) return precheck.ErrorMessage!;
        if (string.IsNullOrWhiteSpace(name)) return "Provide a process image name, e.g. lsass.exe.";

        try
        {
            var output = await dbgEng.ExecuteCommandAsync($"!process 0 0 {name}", timeoutSeconds);
            if (LooksLikeSymbolProblem(output))
            {
                return SymbolProblemMessage("kd_find_process_by_name", output);
            }

            var process = ExtractFirstAddressAfterLabel(output, "PROCESS");
            if (process == null)
            {
                return $"No process named '{name}' found via !process.\n\nRaw output:\n{output}";
            }

            return $"""
ProcessName: {name}
EPROCESS: {process}

Use:
  kd_switch_process process="{process}"
  kd_list_threads process="{process}"

Raw output:
{output}
""";
        }
        catch (OperationCanceledException)
        {
            return $"kd_find_process_by_name timed out after {timeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return $"kd_find_process_by_name failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_list_threads"), Description(
        "List threads for an EPROCESS address using !process <process> 7.")]
    public static async Task<string> KdListThreads(
        StateCoordinator state,
        DbgEngManager dbgEng,

        [Description("EPROCESS address, e.g. ffff81023508c0c0")] string process,

        [Description("Timeout in seconds (default 30)")] int timeoutSeconds = 30,

        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_execute");
        if (precheck != null) return precheck.ErrorMessage!;
        if (string.IsNullOrWhiteSpace(process)) return "Provide an EPROCESS address.";

        try
        {
            var output = await dbgEng.ExecuteCommandAsync($"!process {process} 7", timeoutSeconds);
            if (LooksLikeSymbolProblem(output))
            {
                return SymbolProblemMessage("kd_list_threads", output);
            }

            return $"""
Threads for EPROCESS {process}

Use a listed ETHREAD with:
  kd_switch_thread thread="<ETHREAD>"
  kd_stack thread="<ETHREAD>"

Raw output:
{output}
""";
        }
        catch (OperationCanceledException)
        {
            return $"kd_list_threads timed out after {timeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return $"kd_list_threads failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_switch_process"), Description(
        "Switch debugger process context using .process /r /p <EPROCESS>.")]
    public static async Task<string> KdSwitchProcess(
        StateCoordinator state,
        DbgEngManager dbgEng,

        [Description("EPROCESS address")] string process,

        [Description("Timeout in seconds (default 30)")] int timeoutSeconds = 30,

        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_execute");
        if (precheck != null) return precheck.ErrorMessage!;
        if (string.IsNullOrWhiteSpace(process)) return "Provide an EPROCESS address.";

        try
        {
            var output = await dbgEng.ExecuteCommandAsync($".process /r /p {process}", timeoutSeconds);
            if (LooksLikeSymbolProblem(output))
            {
                return SymbolProblemMessage("kd_switch_process", output);
            }

            return $"""
Switched process context to {process}.

Raw output:
{output}
""";
        }
        catch (OperationCanceledException)
        {
            return $"kd_switch_process timed out after {timeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return $"kd_switch_process failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_switch_thread"), Description(
        "Switch debugger thread context using .thread <ETHREAD>.")]
    public static async Task<string> KdSwitchThread(
        StateCoordinator state,
        DbgEngManager dbgEng,

        [Description("ETHREAD address")] string thread,

        [Description("Timeout in seconds (default 30)")] int timeoutSeconds = 30,

        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_execute");
        if (precheck != null) return precheck.ErrorMessage!;
        if (string.IsNullOrWhiteSpace(thread)) return "Provide an ETHREAD address.";

        try
        {
            var output = await dbgEng.ExecuteCommandAsync($".thread {thread}", timeoutSeconds);
            if (LooksLikeSymbolProblem(output))
            {
                return SymbolProblemMessage("kd_switch_thread", output);
            }

            return $"""
Switched thread context to {thread}.

Raw output:
{output}
""";
        }
        catch (OperationCanceledException)
        {
            return $"kd_switch_thread timed out after {timeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return $"kd_switch_thread failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_stack"), Description(
        "Dump a stack, optionally after switching to an ETHREAD. Default command is kv.")]
    public static async Task<string> KdStack(
        StateCoordinator state,
        DbgEngManager dbgEng,

        [Description("Optional ETHREAD address to switch to before dumping stack")] string? thread = null,

        [Description("Stack command: k, kb, kn, kv, etc. Default kv")] string command = "kv",

        [Description("Timeout in seconds (default 30)")] int timeoutSeconds = 30,

        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_execute");
        if (precheck != null) return precheck.ErrorMessage!;
        if (string.IsNullOrWhiteSpace(command)) command = "kv";

        var (isBlocked, blockedCmd, suggestion) = DbgEngConstants.CheckCommand(command);
        if (isBlocked)
        {
            return $"BLOCKED: The command '{blockedCmd}' changes execution state. {suggestion}";
        }

        try
        {
            var switchOutput = string.Empty;
            if (!string.IsNullOrWhiteSpace(thread))
            {
                switchOutput = await dbgEng.ExecuteCommandAsync($".thread {thread}", timeoutSeconds);
                if (LooksLikeSymbolProblem(switchOutput))
                {
                    return SymbolProblemMessage("kd_stack", switchOutput);
                }
            }

            var stack = await dbgEng.ExecuteCommandAsync(command, timeoutSeconds);
            if (LooksLikeSymbolProblem(stack))
            {
                return SymbolProblemMessage("kd_stack", stack);
            }

            return $"""
StackCommand: {command}
Thread: {(string.IsNullOrWhiteSpace(thread) ? "(current)" : thread)}

{(string.IsNullOrWhiteSpace(switchOutput) ? "" : "--- thread switch ---\n" + switchOutput + "\n")}
--- stack ---
{stack}
""";
        }
        catch (OperationCanceledException)
        {
            return $"kd_stack timed out after {timeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return $"kd_stack failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_find_process_by_name_raw"), Description(
        "Find a process by walking ActiveProcessLinks with caller-provided EPROCESS offsets. Use only when symbols/!process fail.")]
    public static async Task<string> KdFindProcessByNameRaw(
        StateCoordinator state,
        DbgEngManager dbgEng,
        [Description("Image name, e.g. lsass.exe")] string name,
        [Description("Offset of UniqueProcessId in EPROCESS, e.g. 0x480")] string uniquePidOffset,
        [Description("Offset of ActiveProcessLinks in EPROCESS, e.g. 0x488")] string activeLinksOffset,
        [Description("Offset of ImageFileName in EPROCESS, e.g. 0x5e8")] string imageNameOffset,
        [Description("Max list entries to walk (default 512)")] int maxEntries = 512,
        [Description("Timeout in seconds per debugger command (default 30)")] int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_execute");
        if (precheck != null) return precheck.ErrorMessage!;
        if (string.IsNullOrWhiteSpace(name)) return "Provide a process image name, e.g. lsass.exe.";

        try
        {
            var pidOffset = ParseOffset(uniquePidOffset);
            var linksOffset = ParseOffset(activeLinksOffset);
            var nameOffset = ParseOffset(imageNameOffset);

            var initial = await dbgEng.ExecuteCommandAsync("dq nt!PsInitialSystemProcess L1", timeoutSeconds);
            var systemProcess = ExtractFirstHexAddress(initial);
            if (systemProcess == null)
            {
                return $"Could not read nt!PsInitialSystemProcess.\n\nRaw output:\n{initial}";
            }

            var start = ParseAddress(systemProcess);
            var current = start;
            var rows = new List<string>();

            for (var i = 0; i < Math.Clamp(maxEntries, 1, 4096); i++)
            {
                var image = await ReadAsciiAsync(dbgEng, current + nameOffset, 16, timeoutSeconds);
                var pid = await ReadPointerAsync(dbgEng, current + pidOffset, timeoutSeconds);
                rows.Add($"{i:D3} EPROCESS={FormatAddress(current)} PID={FormatAddress(pid)} Image={image}");

                if (image.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return $"""
ProcessName: {image}
EPROCESS: {FormatAddress(current)}
PID: {FormatAddress(pid)}
Method: raw ActiveProcessLinks walk
Offsets:
  UniqueProcessId: 0x{pidOffset:x}
  ActiveProcessLinks: 0x{linksOffset:x}
  ImageFileName: 0x{nameOffset:x}

Recent walk:
{string.Join('\n', rows.TakeLast(16))}
""";
                }

                var flink = await ReadPointerAsync(dbgEng, current + linksOffset, timeoutSeconds);
                var next = flink - linksOffset;
                if (next == start || next == 0)
                {
                    break;
                }
                current = next;
            }

            return $"""
No process named '{name}' found by raw ActiveProcessLinks walk.
Start EPROCESS: {FormatAddress(start)}
Entries walked: {rows.Count}

Walk:
{string.Join('\n', rows)}
""";
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
        {
            return $"kd_find_process_by_name_raw failed: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            return $"kd_find_process_by_name_raw timed out after {timeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return $"kd_find_process_by_name_raw failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_list_threads_raw"), Description(
        "List process threads by walking ThreadListHead with caller-provided offsets. Use only when symbols/!process fail.")]
    public static async Task<string> KdListThreadsRaw(
        StateCoordinator state,
        DbgEngManager dbgEng,
        [Description("EPROCESS address")] string process,
        [Description("Offset of ThreadListHead in EPROCESS, e.g. 0x620")] string threadListHeadOffset,
        [Description("Offset of ThreadListEntry in ETHREAD, e.g. 0x538")] string threadListEntryOffset,
        [Description("Offset of ClientId.UniqueProcess in ETHREAD, e.g. 0x4c8")] string cidPidOffset,
        [Description("Offset of ClientId.UniqueThread in ETHREAD, e.g. 0x4d0")] string cidTidOffset,
        [Description("Max list entries to walk (default 512)")] int maxEntries = 512,
        [Description("Timeout in seconds per debugger command (default 30)")] int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_execute");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var processAddress = ParseAddress(process);
            var listHeadOffset = ParseOffset(threadListHeadOffset);
            var entryOffset = ParseOffset(threadListEntryOffset);
            var pidOffset = ParseOffset(cidPidOffset);
            var tidOffset = ParseOffset(cidTidOffset);
            var listHead = processAddress + listHeadOffset;
            var firstEntry = await ReadPointerAsync(dbgEng, listHead, timeoutSeconds);
            var currentEntry = firstEntry;
            var rows = new List<string>();

            for (var i = 0; i < Math.Clamp(maxEntries, 1, 4096); i++)
            {
                if (currentEntry == listHead || currentEntry == 0)
                {
                    break;
                }

                var ethread = currentEntry - entryOffset;
                var pid = await ReadPointerAsync(dbgEng, ethread + pidOffset, timeoutSeconds);
                var tid = await ReadPointerAsync(dbgEng, ethread + tidOffset, timeoutSeconds);
                rows.Add($"{i:D3} ETHREAD={FormatAddress(ethread)} PID={FormatAddress(pid)} TID={FormatAddress(tid)}");
                currentEntry = await ReadPointerAsync(dbgEng, currentEntry, timeoutSeconds);
            }

            return $"""
Threads for EPROCESS {FormatAddress(processAddress)}
Method: raw ThreadListHead walk
Offsets:
  ThreadListHead: 0x{listHeadOffset:x}
  ThreadListEntry: 0x{entryOffset:x}
  ClientId.UniqueProcess: 0x{pidOffset:x}
  ClientId.UniqueThread: 0x{tidOffset:x}

{string.Join('\n', rows)}
""";
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
        {
            return $"kd_list_threads_raw failed: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            return $"kd_list_threads_raw timed out after {timeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return $"kd_list_threads_raw failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "kd_stack_process_thread"), Description(
        "Switch process and thread context, then dump stack. This breaks/switches context only; it does not guarantee user-mode frames.")]
    public static async Task<string> KdStackProcessThread(
        StateCoordinator state,
        DbgEngManager dbgEng,
        [Description("EPROCESS address")] string process,
        [Description("ETHREAD address")] string thread,
        [Description("Stack command: k, kb, kn, kv, etc. Default kv")] string command = "kv",
        [Description("Timeout in seconds per debugger command (default 30)")] int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_execute");
        if (precheck != null) return precheck.ErrorMessage!;
        if (string.IsNullOrWhiteSpace(command)) command = "kv";

        var (isBlocked, blockedCmd, suggestion) = DbgEngConstants.CheckCommand(command);
        if (isBlocked)
        {
            return $"BLOCKED: The command '{blockedCmd}' changes execution state. {suggestion}";
        }

        try
        {
            var processOutput = await dbgEng.ExecuteCommandAsync($".process /r /p {process}", timeoutSeconds);
            var threadOutput = await dbgEng.ExecuteCommandAsync($".thread {thread}", timeoutSeconds);
            var stackOutput = await dbgEng.ExecuteCommandAsync(command, timeoutSeconds);

            var caveat = processOutput.Contains("PEB address is NULL", StringComparison.OrdinalIgnoreCase) ||
                         threadOutput.Contains("Can't retrieve thread context", StringComparison.OrdinalIgnoreCase) ||
                         !LooksUserModeStackPresent(stackOutput)
                ? "NOTE: user-mode frames were not evident. In KD this can happen if the selected ETHREAD has no recoverable user context, PEB is unavailable, or user symbols/context are not loaded."
                : "User-mode frames may be present; review stack output.";

            return $"""
Process: {process}
Thread: {thread}
Command: {command}
{caveat}

--- .process ---
{processOutput}

--- .thread ---
{threadOutput}

--- stack ---
{stackOutput}
""";
        }
        catch (OperationCanceledException)
        {
            return $"kd_stack_process_thread timed out after {timeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return $"kd_stack_process_thread failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string SaveTranscript(ServerConfig config, string command, string output)
    {
        var directory = string.IsNullOrWhiteSpace(config.KernelDebug.TranscriptDirectory)
            ? Path.Combine(Path.GetTempPath(), "windbg-mcp", "transcripts")
            : config.KernelDebug.TranscriptDirectory;

        Directory.CreateDirectory(directory);

        var timestamp = DateTimeOffset.UtcNow;
        var commandHash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command)))
            .ToLowerInvariant()[..12];
        var path = Path.Combine(
            directory,
            $"{timestamp:yyyyMMdd_HHmmss_fff}_{commandHash}.json");

        var payload = new KdCommandTranscript(timestamp, command, output);
        var json = JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        return path;
    }

    private static ulong ParseOffset(string value)
    {
        return ParseAddress(value);
    }

    private static ulong ParseAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("Provide a hexadecimal address or offset.");
        }

        var normalized = value.Trim().Replace("`", "", StringComparison.Ordinal);
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if (!ulong.TryParse(
                normalized,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new FormatException(
                $"Invalid hexadecimal value '{value}'. Use WinDbg format such as ffff800012345678 or 0x480.");
        }

        return parsed;
    }

    private static string FormatAddress(ulong value)
    {
        return value.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static string? ExtractFirstHexAddress(string output)
    {
        var matches = Regex.Matches(
                output,
                @"(?:0x)?(?:[0-9a-fA-F]{4,}`[0-9a-fA-F]{4,}|[0-9a-fA-F]{8,16})")
            .Select(match => match.Value)
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => matches[1],
        };
    }

    private static async Task<ulong> ReadPointerAsync(
        DbgEngManager dbgEng,
        ulong address,
        int timeoutSeconds)
    {
        var output = await dbgEng.ExecuteCommandAsync($"dq {FormatAddress(address)} L1", timeoutSeconds);
        var matches = Regex.Matches(
                output,
                @"(?:0x)?(?:[0-9a-fA-F]{4,}`[0-9a-fA-F]{4,}|[0-9a-fA-F]{8,16})")
            .Select(match => match.Value)
            .ToList();

        if (matches.Count >= 2)
        {
            return ParseAddress(matches[1]);
        }

        if (matches.Count == 1)
        {
            return ParseAddress(matches[0]);
        }

        throw new InvalidOperationException(
            $"Could not parse pointer at {FormatAddress(address)}.\n\nRaw output:\n{output}");
    }

    private static async Task<string> ReadAsciiAsync(
        DbgEngManager dbgEng,
        ulong address,
        int length,
        int timeoutSeconds)
    {
        var commandAddress = FormatAddress(address);
        var daOutput = await dbgEng.ExecuteCommandAsync($"da {commandAddress} L{length}", timeoutSeconds);
        var quoted = Regex.Match(daOutput, "\"([^\"]*)\"");
        if (quoted.Success)
        {
            return CleanAscii(quoted.Groups[1].Value);
        }

        var dbOutput = await dbgEng.ExecuteCommandAsync($"db {commandAddress} L{length}", timeoutSeconds);
        var parsed = ParseDbAscii(dbOutput, length);
        if (!string.IsNullOrEmpty(parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Could not parse ASCII string at {commandAddress}.\n\nRaw output:\n{daOutput}\n{dbOutput}");
    }

    private static string ParseDbAscii(string output, int maxLength)
    {
        var bytes = new List<byte>();

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var firstSpace = line.IndexOf(' ', StringComparison.Ordinal);
            if (firstSpace < 0 || firstSpace + 1 >= line.Length)
            {
                continue;
            }

            var payload = line[(firstSpace + 1)..].TrimStart().Replace('-', ' ');
            var asciiStart = payload.IndexOf("  ", StringComparison.Ordinal);
            if (asciiStart >= 0)
            {
                payload = payload[..asciiStart];
            }

            foreach (var token in payload.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length != 2 ||
                    !byte.TryParse(
                        token,
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    continue;
                }

                bytes.Add(parsed);
                if (bytes.Count >= maxLength)
                {
                    return CleanAscii(bytes);
                }
            }
        }

        return CleanAscii(bytes);
    }

    private static string CleanAscii(IEnumerable<byte> bytes)
    {
        var chars = bytes
            .TakeWhile(value => value != 0)
            .Select(value => value is >= 0x20 and <= 0x7e ? (char)value : '.')
            .ToArray();
        return new string(chars).Trim();
    }

    private static string CleanAscii(string value)
    {
        var nul = value.IndexOf('\0', StringComparison.Ordinal);
        if (nul >= 0)
        {
            value = value[..nul];
        }

        return value.Trim();
    }

    private static bool LooksUserModeStackPresent(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        string[] directUserIndicators =
        [
            "ntdll!",
            "kernel32!",
            "kernelbase!",
            "wow64!",
            "ucrtbase!",
            "msvcrt!",
            ".exe!",
        ];

        if (directUserIndicators.Any(indicator =>
                output.Contains(indicator, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var line in output.Split('\n'))
        {
            var bang = line.IndexOf('!');
            if (bang <= 0)
            {
                continue;
            }

            var beforeBang = line[..bang].Trim();
            var moduleStart = beforeBang.LastIndexOfAny([' ', '\t']);
            var module = moduleStart >= 0 ? beforeBang[(moduleStart + 1)..] : beforeBang;
            if (module.Length == 0)
            {
                continue;
            }

            string[] kernelModules =
            [
                "nt",
                "hal",
                "kd",
                "kdnic",
                "tcpip",
                "ndis",
                "netio",
                "fltmgr",
                "fileinfo",
                "ci",
                "clfs",
                "win32k",
                "win32kbase",
                "win32kfull",
                "dxgkrnl",
                "storport",
                "volmgr",
                "partmgr",
                "afd",
                "acpi",
                "wdf01000",
            ];

            if (!kernelModules.Contains(module, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record KdCommandTranscript(
        DateTimeOffset TimestampUtc,
        string Command,
        string Output);

    private static bool LooksLikeSymbolProblem(string output)
    {
        return output.Contains("symbols are incorrect", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("symbol file could not be found", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("export symbols", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("type information missing", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Unable to get", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Unable to read", StringComparison.OrdinalIgnoreCase);
    }

    private static string SymbolProblemMessage(string toolName, string rawOutput)
    {
        return $"""
{toolName} could not complete because kernel symbols appear incomplete.

Run:
  kd_symbol_status

Then, if needed:
  kd_execute command=".symfix"
  kd_execute command=".reload /f nt"
  kd_execute command="lm vm nt"

Raw output:
{rawOutput}
""";
    }

    private static string? ExtractFirstAddressAfterLabel(string output, string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            output,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(label)}\s+([0-9a-fA-F`]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    [McpServerTool(Name = "kd_wait_for_event"), Description(
        "Wait for a debug event (breakpoint hit, exception, etc.) with a timeout. " +
        "Use this after kd_continue + set_breakpoint to wait for the breakpoint to be hit. " +
        "ALWAYS returns within timeout — never hangs. If no event, target keeps running.")]
    public static async Task<string> KdWaitForEvent(
        StateCoordinator state,
        DbgEngManager dbgEng,
        [Description("How many seconds to wait for an event (default 10, max 120)")] int timeoutSeconds = 10,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("kd_wait_for_event");
        if (precheck != null) return precheck.ErrorMessage!;

        // Clamp timeout
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 120);

        try
        {
            var result = await dbgEng.WaitForEventAsync(timeoutSeconds);

            // Check for BSOD if we received an event
            if (result.Contains("halted", StringComparison.OrdinalIgnoreCase))
            {
                state.SetKdBroken("Debug event");
                var (isBugcheck, bugcheckCode) = await dbgEng.DetectBugcheckAsync();
                if (isBugcheck)
                {
                    state.SetBsodDetected(bugcheckCode);
                    return result + $"\n\nWARNING: BSOD DETECTED (bugcheck {bugcheckCode}). " +
                           "The OS has crashed. Use kd_execute('!analyze -v') to investigate.";
                }
            }
            else if (result.Contains("Target is still running", StringComparison.OrdinalIgnoreCase))
            {
                state.SetKdRunning();
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return $"Wait cancelled after {timeoutSeconds}s. Target is still running.";
        }
        catch (Exception ex)
        {
            return $"kd_wait_for_event failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
