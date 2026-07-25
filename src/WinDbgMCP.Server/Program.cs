using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.Guest;
using WinDbgMCP.Server.KernelDebug;
using WinDbgMCP.Server.State;
using WinDbgMCP.Server.Tools;
using WinDbgMCP.Server.UserModeDebug;
using WinDbgMCP.Server.Vmware;

var builder = Host.CreateApplicationBuilder(args);

// Load optional configuration relative to the executable, not the caller's CWD.
// A published single-file EXE can run without a sidecar appsettings.json when
// values are provided through command-line args or WINDBG_MCP_* environment variables.
var exeDir = AppContext.BaseDirectory;
builder.Configuration
    .AddJsonFile(Path.Combine(exeDir, "appsettings.json"), optional: true, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "WINDBG_MCP_")
    .AddCommandLine(args);

// MCP servers use stdio. Keep logs on stderr so stdout remains JSON-RPC only.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

var config = new ServerConfig();
builder.Configuration.Bind(config);
ApplyPortableOverrides(config);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<VmwareManager>();
builder.Services.AddSingleton<DbgEngThread>(sp => new DbgEngThread(sp.GetRequiredService<ILogger<DbgEngThread>>()));
builder.Services.AddSingleton<DbgEngManager>();
builder.Services.AddSingleton<GuestExecManager>();
builder.Services.AddSingleton<FridaManager>();
builder.Services.AddSingleton<DbgsrvManager>();
builder.Services.AddSingleton<TtdManager>();
builder.Services.AddSingleton<StateCoordinator>(sp =>
{
    var stateConfig = sp.GetRequiredService<ServerConfig>();
    var logger = sp.GetRequiredService<ILogger<StateCoordinator>>();
    var vmware = sp.GetRequiredService<VmwareManager>();
    var dbgEng = sp.GetRequiredService<DbgEngManager>();
    var frida = sp.GetRequiredService<FridaManager>();
    var dbgsrv = sp.GetRequiredService<DbgsrvManager>();

    var coordinator = new StateCoordinator(stateConfig, logger)
    {
        GetVmPowerStateAsync = () => vmware.GetPowerStateAsync(),
        AreToolsRunningAsync = timeout => vmware.AreToolsRunningAsync(timeout),
        IsDbgEngConnected = () => dbgEng.IsConnected,
        GetDbgEngExecutionStatus = () => dbgEng.GetExecutionStatus(),
        GetPendingEventCount = () => dbgEng.PendingEventCount,
        IsFridaAttached = () => frida.IsAttached,
        GetFridaTargetName = () => frida.AttachedProcessName,
        IsDbgsrvConnected = () => dbgsrv.IsConnected,
        GetDbgsrvAttachedPid = () => dbgsrv.AttachedPid,
        CleanupKdSession = () => dbgEng.ResetConnectionState(),
        CleanupFridaSession = () => frida.Dispose(),
        CleanupDbgsrvSession = () => dbgsrv.Dispose(),
    };

    return coordinator;
});

var toolTypes = new List<Type>
{
    typeof(KernelDebugTools),
    typeof(MetaTools),
};

var serverSideUserModeToolsEnabled = config.UserModeDebug.ServerSideToolsEnabled || config.Vm.VmwareEnabled;
if (serverSideUserModeToolsEnabled)
{
    toolTypes.Add(typeof(UserModeDebugTools));
}

if (config.Vm.VmwareEnabled)
{
    toolTypes.Insert(0, typeof(VmTools));
    toolTypes.Add(typeof(GuestTools));

    if (string.IsNullOrEmpty(config.Vm.VmPassword))
    {
        toolTypes.Add(typeof(VmScreenshotTool));
    }
}

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "WinDbgMCP", Version = "1.0.0-alpha" };
        options.ServerInstructions = BuildServerInstructions(config);
    })
    .WithStdioServerTransport()
    .WithTools(toolTypes);

await builder.Build().RunAsync();

static void ApplyPortableOverrides(ServerConfig config)
{
    ApplyString("WINDBG_MCP_TARGET_HOST", value => config.Target.Host = value);
    ApplyString("WINDBG_MCP_KDNET_KEY", value => config.KernelDebug.Kdnet.Key = value);
    ApplyString("WINDBG_MCP_SYMBOL_PATH", value => config.KernelDebug.SymbolPath = value);
    ApplyString("WINDBG_MCP_TRANSCRIPT_DIRECTORY", value => config.KernelDebug.TranscriptDirectory = value);
    ApplyInt("WINDBG_MCP_KDNET_PORT", value => config.KernelDebug.Kdnet.Port = value);
    ApplyInt("WINDBG_MCP_FRIDA_PORT", value => config.Guest.FridaPort = value);
    ApplyBool("WINDBG_MCP_VMWARE_ENABLED", value => config.Vm.VmwareEnabled = value);
    ApplyBool("WINDBG_MCP_SERVER_SIDE_UMD", value => config.UserModeDebug.ServerSideToolsEnabled = value);

    static void ApplyString(string name, Action<string> apply)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            apply(value);
    }

    static void ApplyInt(string name, Action<int> apply)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(value, out var parsed))
            apply(parsed);
    }

    static void ApplyBool(string name, Action<bool> apply)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (bool.TryParse(value, out var parsed))
            apply(parsed);
    }
}

static string BuildServerInstructions(ServerConfig config)
{
    var serverSideUserModeToolsEnabled = config.UserModeDebug.ServerSideToolsEnabled || config.Vm.VmwareEnabled;
    var targetHost = !string.IsNullOrWhiteSpace(config.Target.Host)
        ? config.Target.Host
        : config.Vm.GuestIpAddress;
    var targetLabel = string.IsNullOrWhiteSpace(targetHost) ? "<TARGET_IP>" : targetHost;

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("# WinDbgMCP");
    sb.AppendLine();

    if (!config.Vm.VmwareEnabled)
    {
        sb.AppendLine("This server is running without VMware/vmrun integration.");
        sb.AppendLine("Use it for kernel debugging over KDNET.");
        sb.AppendLine("Do not attempt VM lifecycle, guest command, file transfer, screenshot, snapshot, Frida, dbgsrv, or TTD workflows through this MCP server in this deployment.");
    }
    else
    {
        sb.AppendLine("This server controls a VMware-backed Windows target plus kernel and user-mode debugging tools.");
    }

    sb.AppendLine();
    sb.AppendLine("## Topology");
    sb.AppendLine("- MCP clients connect to mcp-proxy on the Windows debugger host.");
    sb.AppendLine("- The .NET server runs on the debugger host and uses DbgEng locally.");
    sb.AppendLine($"- KDNET listens on UDP port {config.KernelDebug.Kdnet.Port} on the debugger host; the target is configured to connect to that host.");
    sb.AppendLine($"- Frida is not MCP. In this lab, access frida-server directly from the operator/LLM host at {targetLabel}:{config.Guest.FridaPort}; the debugger host does not need Frida CLI tools.");
    sb.AppendLine();

    sb.AppendLine("## Safe starting point");
    sb.AppendLine("- Call `get_system_state` first.");
    sb.AppendLine("- For kernel inspection, use `kd_connect`, `kd_break` if needed, `kd_execute` for inspection commands, `kd_continue`, `kd_wait_for_event`, and `kd_disconnect`.");
    sb.AppendLine("- Do not send execution-control commands such as `g`, `p`, `t`, `q`, `.detach`, `.reboot`, or `.restart` through `kd_execute`; use the dedicated MCP tools.");
    if (serverSideUserModeToolsEnabled)
    {
        sb.AppendLine("- Server-side user-mode tools are enabled; use `umd_frida_attach`/`umd_frida` only if the debugger host has Frida CLI tools and can reach the target.");
    }
    else
    {
        sb.AppendLine("- Server-side user-mode MCP tools are not registered. Use direct Frida commands from the operator/LLM host instead.");
    }

    if (config.Vm.VmwareEnabled)
    {
        sb.AppendLine();
        sb.AppendLine("## VMware-backed tools");
        sb.AppendLine("- `vm_*` tools control VMware lifecycle and snapshots.");
        sb.AppendLine("- `guest_*` tools run commands and transfer files through VMware guest operations.");
        sb.AppendLine("- Snapshot restore destroys active KD, Frida, and dbgsrv sessions; reconnect after restore.");
    }

    sb.AppendLine();
    sb.AppendLine("## Crash handling");
    sb.AppendLine("- If `get_system_state` reports a bugcheck, inspect with `kd_execute(\"!analyze -v\")`, `kd_execute(\"k\")`, and `kd_execute(\"r\")`.");
    sb.AppendLine("- `kd_disconnect` detaches and resumes the target. Remove temporary breakpoints before disconnecting when practical.");

    return sb.ToString();
}
