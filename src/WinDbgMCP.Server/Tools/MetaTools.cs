using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.State;

namespace WinDbgMCP.Server.Tools;

[McpServerToolType]
public static class MetaTools
{
    [McpServerTool(Name = "get_system_state", ReadOnly = true), Description(
        "Returns current WinDbgMCP state: deployment mode, target host, kernel debugger status, " +
        "direct Frida note, and availability flags. Always safe to call first.")]
    public static async Task<string> GetSystemState(
        StateCoordinator state,
        ServerConfig config,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await state.RefreshStateAsync();
        }
        catch (Exception ex)
        {
            return "=== SYSTEM STATE ===\n\n" +
                   $"State refresh failed: {ex.GetType().Name}: {ex.Message}\n" +
                   "The MCP server is reachable, but one of its local probes failed.";
        }

        var s = state.State;
        var targetHost = !string.IsNullOrWhiteSpace(config.Target.Host)
            ? config.Target.Host
            : config.Vm.GuestIpAddress;

        var sb = new StringBuilder();
        sb.AppendLine("=== SYSTEM STATE ===");
        sb.AppendLine();
        sb.AppendLine($"Deployment:          {(config.Vm.VmwareEnabled ? "VMware/vmrun enabled" : "externally managed target (VmwareEnabled=false)")}");
        sb.AppendLine($"Target Host:         {(string.IsNullOrWhiteSpace(targetHost) ? "unknown" : targetHost)}");
        sb.AppendLine($"Direct Frida:        {(string.IsNullOrWhiteSpace(targetHost) ? "target host unknown" : $"operator host -> {targetHost}:{config.Guest.FridaPort}")}");
        sb.AppendLine($"Server UMD Tools:    {(config.UserModeDebug.ServerSideToolsEnabled || config.Vm.VmwareEnabled ? "registered" : "not registered")}");
        sb.AppendLine();

        if (config.Vm.VmwareEnabled)
        {
            sb.AppendLine($"VM Power:            {s.VmPower}");
            sb.AppendLine($"VMware Tools:        {s.VmTools}");
            sb.AppendLine($"VM IP Address:       {s.VmIpAddress ?? "unknown"}");
            sb.AppendLine($"Guest Ops Available: {s.GuestOpsAvailable}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("VMware Backend:      disabled");
            sb.AppendLine("VM/GUEST Tools:      not registered in this deployment");
            sb.AppendLine("Target Reachability: managed outside WinDbgMCP");
            sb.AppendLine();
        }

        sb.AppendLine($"KD Connected:        {s.KdConnected}");
        sb.AppendLine($"KD Transport:        {s.KdTransportType}");
        sb.AppendLine($"KD Exec Status:      {s.KdExecStatus}");
        sb.AppendLine($"KD Wait Pending:     {s.KdWaitPending}");
        sb.AppendLine($"Pending Events:      {s.PendingEventCount}");
        sb.AppendLine($"Bugcheck:            {s.IsBugcheck}");
        sb.AppendLine();

        if (config.UserModeDebug.ServerSideToolsEnabled || config.Vm.VmwareEnabled)
        {
            sb.AppendLine($"Frida:               {(s.FridaState == null ? "not attached" : s.FridaState)}");
            sb.AppendLine($"dbgsrv:              {(s.DbgsrvState == null ? "not connected" : s.DbgsrvState)}");
        }
        else
        {
            sb.AppendLine("Frida:               direct from operator host, outside this MCP server");
            sb.AppendLine("dbgsrv:              server-side MCP tool not registered");
        }

        if (s.UserDebugSessions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Active Debug Sessions:");
            foreach (var session in s.UserDebugSessions)
            {
                sb.AppendLine($" - [{session.Type}] PID {session.Pid} ({session.ProcessName})");
            }
        }

        return sb.ToString();
    }
}
