namespace WinDbgMCP.Server.Configuration;

/// <summary>
/// Root configuration model for WinDbgMCP server.
/// Loaded from appsettings.json.
/// </summary>
public sealed class ServerConfig
{
    public VmConfig Vm { get; set; } = new();
    public TargetConfig Target { get; set; } = new();
    public UserModeDebugConfig UserModeDebug { get; set; } = new();
    public KernelDebugConfig KernelDebug { get; set; } = new();
    public GuestConfig Guest { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public TimeoutConfig Timeouts { get; set; } = new();
}

public sealed class TargetConfig
{
    /// <summary>
    /// Debuggee/target IP or hostname. This is where target-side services such as
    /// frida-server may listen. It is not an MCP endpoint.
    /// </summary>
    public string Host { get; set; } = string.Empty;
}

public sealed class UserModeDebugConfig
{
    /// <summary>
    /// Expose server-side user-mode tools that run frida/cdb from the Windows
    /// debugger host. Leave false when Frida is accessed directly from the
    /// operator/LLM host.
    /// </summary>
    public bool ServerSideToolsEnabled { get; set; } = false;
}

public sealed class VmConfig
{
    /// <summary>
    /// When false, the server never invokes vmrun. VM-lifecycle and guest-OS tools
    /// (vm_*, guest_*, umd_ttd) are rejected, but kernel debugging over KDNET
    /// keeps working against an externally-managed target.
    /// </summary>
    public bool VmwareEnabled { get; set; } = true;

    /// <summary>
    /// Legacy target IP field. Prefer Target.Host for new configurations.
    /// When set, it is used to reach frida-server directly instead of discovering
    /// the IP via vmrun.
    /// </summary>
    public string GuestIpAddress { get; set; } = string.Empty;

    public string VmxPath { get; set; } = string.Empty;
    public string VmrunPath { get; set; } = @"C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe";
    public string VmPassword { get; set; } = string.Empty;
    public string GuestUsername { get; set; } = string.Empty;
    public string GuestPassword { get; set; } = string.Empty;
    public bool Headless { get; set; } = true;
}

public sealed class KernelDebugConfig
{
    /// <summary>"kdnet" or "serial".</summary>
    public string Transport { get; set; } = "kdnet";
    public KdnetConfig Kdnet { get; set; } = new();
    public SerialConfig Serial { get; set; } = new();
    public string SymbolPath { get; set; } = @"srv*C:\Symbols*https://msdl.microsoft.com/download/symbols";
    public string TranscriptDirectory { get; set; } = @"C:\tmp\windbg-mcp\transcripts";
    public bool ExitProcessOnDbgEngWedge { get; set; }
}

public sealed class KdnetConfig
{
    public int Port { get; set; } = 50000;
    public string Key { get; set; } = string.Empty;
}

public sealed class SerialConfig
{
    public string PipeName { get; set; } = @"\\.\pipe\com_1";
}

public sealed class GuestConfig
{
    public int FridaPort { get; set; } = 27042;
    public int DbgsrvPort { get; set; } = 5064;
}

public sealed class SecurityConfig
{
    /// <summary>
    /// Snapshot deletion is disabled by default. It must be explicitly enabled.
    /// </summary>
    public bool SnapshotDeleteEnabled { get; set; } = false;

    /// <summary>
    /// Snapshots in this list cannot be deleted or overwritten.
    /// </summary>
    public List<string> ProtectedSnapshots { get; set; } = new();

    /// <summary>
    /// Prevent deleting the final remaining snapshot.
    /// </summary>
    public bool PreventLastSnapshotDeletion { get; set; } = true;

    /// <summary>
    /// Known-good snapshot name, if VMware snapshot workflows are enabled.
    /// </summary>
    public string DefaultSnapshotName { get; set; } = string.Empty;
}

public sealed class TimeoutConfig
{
    // VMware / vmrun
    public int VmStartSeconds { get; set; } = 60;
    public int VmStopSeconds { get; set; } = 30;
    public int VmPauseResumeSeconds { get; set; } = 10;
    public int VmSnapshotCreateSeconds { get; set; } = 120;
    public int VmSnapshotRestoreSeconds { get; set; } = 60;
    public int VmScreenshotSeconds { get; set; } = 10;
    public int VmToolsCheckSeconds { get; set; } = 5;
    public int VmGetIpSeconds { get; set; } = 10;

    // Kernel debugging
    public int KdConnectSeconds { get; set; } = 30;
    public int KdInitialBreakSeconds { get; set; } = 15;
    public int KdBreakSeconds { get; set; } = 10;
    public int KdStepSeconds { get; set; } = 10;
    public int KdCommandExecuteSeconds { get; set; } = 30;
    public int KdMemoryReadSeconds { get; set; } = 10;
    public int KdMemoryWriteSeconds { get; set; } = 10;
    public int KdWaitForBreakpointSeconds { get; set; } = 10;

    // Guest / user-mode debugging
    public int GuestCommandSeconds { get; set; } = 60;
    public int GuestFileTransferSeconds { get; set; } = 120;
    public int GuestListProcessesSeconds { get; set; } = 15;
    public int GuestKillProcessSeconds { get; set; } = 10;
    public int FridaAttachSeconds { get; set; } = 15;
    public int FridaScriptSeconds { get; set; } = 30;
    public int DbgsrvConnectSeconds { get; set; } = 15;
    public int TtdRecordMinutes { get; set; } = 5;
}
