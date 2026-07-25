using System.ComponentModel;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.State;
using WinDbgMCP.Server.UserModeDebug;

namespace WinDbgMCP.Server.Tools;

[McpServerToolType]
public static class UserModeDebugTools
{
    // ═══════════════════════════════════════════════════════════════
    //  FRIDA TOOLS
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "umd_frida_attach"), Description(
        "Attach Frida to a process on the target/debuggee for dynamic instrumentation. " +
        "Requires: frida-tools installed on host (pip install frida-tools), " +
        "frida-server.exe running on the target/debuggee. " +
        "After attaching, use umd_frida to inject scripts, hook functions, etc.")]
    public static async Task<string> UmdFridaAttach(
        StateCoordinator state,
        FridaManager frida,
        [Description("Process name to attach to (e.g., 'notepad.exe'). Mutually exclusive with pid.")] string? processName = null,
        [Description("Process ID to attach to. Mutually exclusive with processName.")] int? pid = null,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("umd_frida_attach");
        if (precheck != null) return precheck.ErrorMessage!;

        if (processName == null && pid == null)
            return "Provide either processName or pid to attach to.";

        try
        {
            if (pid.HasValue)
                return await frida.AttachAsync(pid.Value, ct);
            else
                return await frida.AttachByNameAsync(processName!, ct);
        }
            catch (OperationCanceledException)
            {
                return "umd_frida_attach timed out. Is frida-server running on the target/debuggee?";
            }
        catch (Exception ex)
        {
            return $"umd_frida_attach failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "umd_frida"), Description(
        "Perform Frida operations on an attached process. " +
        "Actions: 'inject' (run JS script), 'eval' (one-liner JS), " +
        "'list' (list target processes via frida-ps), 'detach', " +
        "'inject_bg' (start persistent background hook session), " +
        "'collect_bg' (read output from background session), " +
        "'stop_bg' (stop background session).")]
    public static async Task<string> UmdFrida(
        StateCoordinator state,
        FridaManager frida,
        [Description("Action: 'inject', 'eval', 'list', 'detach', 'inject_bg', 'collect_bg', 'stop_bg'")] string action,
        [Description("For 'inject'/'inject_bg': JavaScript code to inject. For 'eval': JS expression.")] string? code = null,
        [Description("Timeout in seconds (default 30). For 'inject' only.")] int timeoutSeconds = 30,
        [Description("For 'inject': if true, hooks persist in the target process after this call returns. " +
                     "Use this when installing Interceptor.attach/replace hooks that should survive across " +
                     "multiple umd_frida calls. Default false.")] bool eternalize = false,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("umd_frida");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            return action.ToLowerInvariant() switch
            {
                "inject" => code == null
                    ? "Provide 'code' parameter with JavaScript to inject."
                    : await frida.InjectScriptAsync(code, timeoutSeconds, eternalize, ct),

                "eval" => code == null
                    ? "Provide 'code' parameter with JavaScript expression to evaluate."
                    : await frida.EvalAsync(code, timeoutSeconds, ct),

                "list" => await frida.ListProcessesAsync(ct),

                "detach" => frida.Detach(),

                "inject_bg" => code == null
                    ? "Provide 'code' parameter with JavaScript to inject in background."
                    : await frida.InjectBackgroundAsync(code, ct),

                "collect_bg" => frida.CollectBackgroundOutput(),

                "stop_bg" => frida.StopBackgroundSession(),

                _ => $"Unknown action '{action}'. Use: inject, eval, list, detach, inject_bg, collect_bg, stop_bg."
            };
        }
        catch (OperationCanceledException)
        {
            return "umd_frida timed out.";
        }
        catch (Exception ex)
        {
            return $"umd_frida failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public static string UmdFridaSkill()
    {
        return """
# Frida via WinDbgMCP

Frida is not MCP. The MCP client talks to WinDbgMCP on the debugger host.
WinDbgMCP shells out to host-side frida/frida-ps, which connect to frida-server
on the target/debuggee at Target.Host:Guest.FridaPort.

Required:
- Debugger host: frida-tools installed (`pip install frida-tools`).
- Target/debuggee: frida-server running and reachable from the debugger host.
- appsettings.json: Target.Host set to the target/debuggee IP or hostname.

Supported actions:
- list: list target processes via frida-ps.
- eval: run a one-line JavaScript expression in the attached process.
- inject: run a JavaScript script for timeoutSeconds.
- inject_bg: start a persistent background hook session.
- collect_bg: read output from a background session.
- stop_bg: stop the background session.
- detach: detach from the current process.

Basic workflow:
1. get_system_state
2. umd_frida(action="list")
3. umd_frida_attach(processName="notepad.exe") or umd_frida_attach(pid=1234)
4. umd_frida(action="eval", code="`${Process.arch}|pid=${Process.id}`")
5. umd_frida(action="detach")

Useful expressions:
- Process.id
- Process.arch
- Process.platform
- Process.enumerateModules().map(m => m.name)
- Process.getModuleByName('ntdll.dll').base
- Process.getModuleByName('kernel32.dll').getExportByName('CreateFileW')

Frida 17 export lookup:
```js
const addr = Process.getModuleByName('ntdll.dll').getExportByName('NtClose');
// or search globally:
const addr2 = Module.getGlobalExportByName('NtClose');
```

Persistent hook example:
```js
const addr = Process.getModuleByName('kernel32.dll').getExportByName('CreateFileW');
Interceptor.attach(addr, {
  onEnter(args) {
    console.log('CreateFileW ' + args[0].readUtf16String());
  }
});
```
Use inject_bg for persistent hooks, then collect_bg to retrieve output and
stop_bg when finished.

Troubleshooting:
- Frida list/attach times out: verify debugger-host connectivity to Target.Host:Guest.FridaPort.
- Cannot determine target host: set Target.Host in appsettings.json.
- Attach cannot find process: run umd_frida(action="list") and use the exact process name or PID.
- Hook appears installed but readU8/readByteArray does not show trampoline bytes: Frida can hide its own patching from process-local reads. Verify by callback output or with KD memory inspection.
""";
    }

    // ═══════════════════════════════════════════════════════════════
    //  DBGSRV TOOLS
    // ═══════════════════════════════════════════════════════════════

    public static string UmdDbgsrvSkill()
    {
        return """
# dbgsrv via WinDbgMCP

Use dbgsrv only when a dbgsrv.exe instance is already running on the target/debuggee.
This deployment does not use VMware guest commands to start or manage dbgsrv.

Topology:
- MCP client talks to the debugger host.
- The debugger host runs cdb.exe.
- cdb.exe connects to dbgsrv.exe on the target/debuggee.

Required:
- Debugger host: Windows SDK / WDK Debuggers installed; cdb.exe available.
- Target/debuggee: dbgsrv.exe running, usually `dbgsrv.exe -t tcp:port=5064`.
- Network: debugger host can reach Target.Host:Guest.DbgsrvPort.

Important limitations:
- The dbgsrv workflow is independent from KD.
- Each command starts a fresh cdb.exe process; command-local state does not persist.
- Noninvasive attach can inspect memory/modules/stacks but cannot set breakpoints,
  step, or write memory. Use Frida for dynamic instrumentation.

Common commands:
- lm: list modules
- lm m kernel32: module-specific listing
- x kernel32!*Create*: symbol lookup
- k / ~*k: stack traces
- r: registers
- db / dq / du: memory reads
- !handle, !peb, !teb: user-mode inspection

Troubleshooting:
- Connection refused: dbgsrv is not running on the target or firewall blocks the port.
- AttachProcess failed: verify the PID exists and try a non-protected process.
- cdb.exe not found: install Windows SDK / WDK Debuggers on the debugger host.
- Not connected to dbgsrv: call umd_dbgsrv_connect first with the target/debuggee IP.
""";
    }

    [McpServerTool(Name = "umd_dbgsrv_connect"), Description(
        "Connect to dbgsrv.exe running on the target/debuggee for remote user-mode debugging. " +
        "Requires: dbgsrv.exe running on the target (C:\\Tools\\DbgSrv\\dbgsrv.exe -t tcp:port=5064), " +
        "and cdb.exe installed on the host (Windows SDK Debuggers). " +
        "Uses cdb.exe externally — fully independent from kernel debugging. " +
        "Both kernel debug and user-mode debug can be active simultaneously.")]
    public static async Task<string> UmdDbgsrvConnect(
        StateCoordinator state,
        DbgsrvManager dbgsrv,
        [Description("Target/debuggee IP address")] string vmIpAddress,
        [Description("dbgsrv TCP port (default 5064)")] int port = 5064,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("umd_dbgsrv_connect");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            return await dbgsrv.ConnectAsync(vmIpAddress, port, ct);
        }
        catch (OperationCanceledException)
        {
            return "umd_dbgsrv_connect timed out. Is dbgsrv.exe running on the target/debuggee? " +
                   "Start dbgsrv on the target manually and verify debugger-host connectivity to the dbgsrv port.";
        }
        catch (Exception ex)
        {
            return $"umd_dbgsrv_connect failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "umd_dbgsrv_execute"), Description(
        "Execute operations via the dbgsrv remote user-mode debug connection. " +
        "Actions: 'attach' (attach to PID), 'command' (run WinDbg command), " +
        "'detach', 'disconnect'.")]
    public static async Task<string> UmdDbgsrvExecute(
        StateCoordinator state,
        DbgsrvManager dbgsrv,
        [Description("Action: 'attach', 'command', 'detach', 'disconnect'")] string action,
        [Description("For 'attach': PID. For 'command': WinDbg command string.")] string? argument = null,
        [Description("Timeout in seconds (default 30)")] int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("umd_dbgsrv_execute");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            return action.ToLowerInvariant() switch
            {
                "attach" => argument == null || !uint.TryParse(argument, out var pid)
                    ? "Provide 'argument' with the PID to attach to."
                    : await dbgsrv.AttachToProcessAsync(pid),

                "command" => argument == null
                    ? "Provide 'argument' with the WinDbg command to execute."
                    : await dbgsrv.ExecuteCommandAsync(argument, timeoutSeconds),

                "detach" => await dbgsrv.DetachAsync(),

                "disconnect" => await dbgsrv.DisconnectAsync(),

                _ => $"Unknown action '{action}'. Use: attach, command, detach, disconnect."
            };
        }
        catch (OperationCanceledException)
        {
            return $"umd_dbgsrv_execute '{action}' timed out. The DbgEng thread may be busy. " +
                   "Try again, or disconnect and reconnect.";
        }
        catch (Exception ex)
        {
            return $"umd_dbgsrv_execute '{action}' failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  TTD TOOLS
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "umd_ttd"), Description(
        "Manage Time Travel Debugging recordings on the target/debuggee. " +
        "Actions: 'record_launch' (start a process under TTD), " +
        "'record_attach' (attach TTD to running PID), 'stop' (stop recording), " +
        "'retrieve' (copy trace to host), 'list' (list trace files). " +
        "Requires VMware guest operations and is unavailable when Vm.VmwareEnabled=false.")]
    public static async Task<string> UmdTtd(
        StateCoordinator state,
        TtdManager ttd,
        [Description("Action: 'record_launch', 'record_attach', 'stop', 'retrieve', 'list'")] string action,
        [Description("For 'record_launch': target executable path in guest. " +
                     "For 'record_attach': PID. " +
                     "For 'retrieve': guest trace file path.")] string? target = null,
        [Description("For 'record_launch': command line arguments")] string? arguments = null,
        [Description("For 'retrieve': host output path")] string? outputPath = null,
        [Description("Timeout in seconds (default 300 for recordings)")] int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("umd_ttd");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            return action.ToLowerInvariant() switch
            {
                "record_launch" => target == null
                    ? "Provide 'target' with the executable path in the guest."
                    : await ttd.RecordLaunchAsync(target, arguments ?? "", timeoutSeconds: timeoutSeconds, ct: ct),

                "record_attach" => target == null || !uint.TryParse(target, out var pid)
                    ? "Provide 'target' with the PID to record."
                    : await ttd.RecordAttachAsync(pid, timeoutSeconds: timeoutSeconds, ct: ct),

                "stop" => await ttd.StopRecordingAsync(ct),

                "retrieve" => target == null || outputPath == null
                    ? "Provide 'target' (guest trace path) and 'outputPath' (host path)."
                    : await ttd.RetrieveTraceAsync(target, outputPath, ct),

                "list" => await ttd.ListTracesAsync(ct: ct),

                _ => $"Unknown action '{action}'. Use: record_launch, record_attach, stop, retrieve, list."
            };
        }
        catch (OperationCanceledException)
        {
            return "umd_ttd timed out.";
        }
        catch (Exception ex)
        {
            return $"umd_ttd failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "umd_ttd_query"), Description(
        "Query a TTD trace file on the host using WinDbg/DbgEng TTD queries. " +
        "The trace must first be retrieved from the guest via umd_ttd(action='retrieve'). " +
        "NOTE: This tool is not yet implemented — use WinDbg Preview to open .run files.")]
    public static Task<string> UmdTtdQuery(
        [Description("Path to the .run trace file on the host")] string tracePath,
        [Description("TTD query (e.g., 'dx @$cursession.TTD.Calls(\"kernel32!CreateFileW\")')")] string query)
    {
        // TTD query via DbgEng requires opening the trace as a dump target
        // in a separate DbgEng session. This is complex to implement and is
        // deferred to a later phase. For now, suggest using WinDbg Preview.
        return Task.FromResult(
            "umd_ttd_query is not yet implemented. To analyze TTD traces:\n" +
            $"1. Open the trace in WinDbg Preview: File > Open Trace > {tracePath}\n" +
            "2. Run TTD queries like:\n" +
            "   dx @$cursession.TTD.Calls(\"kernel32!CreateFileW\")\n" +
            "   dx @$cursession.TTD.Memory(0x12345, 0x12345+4, \"w\")\n" +
            "   !tt 0:0  (go to start of trace)\n" +
            "   !tt 100  (go to end of trace)\n" +
            "This feature will be implemented in a future update.");
    }
}
