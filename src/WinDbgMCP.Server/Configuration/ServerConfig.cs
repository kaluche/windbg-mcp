namespace WinDbgMCP.Server.Configuration;

/// <summary>
/// Root configuration model for the WinDbgMCP server.
/// Loaded from appsettings.json.
/// </summary>
public sealed class ServerConfig
{
    public VmConfig Vm { get; set; } = new();
    public KernelDebugConfig KernelDebug { get; set; } = new();
    public GuestConfig Guest { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public TimeoutConfig Timeouts { get; set; } = new();
}

public sealed class VmConfig
{
    public string VmxPath { get; set; } = string.Empty;
    public string VmrunPath { get; set; } = @"C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe";
    public string GuestUsername { get; set; } = string.Empty;
    public string GuestPassword { get; set; } = string.Empty;
    public bool Headless { get; set; } = true;
}

public sealed class KernelDebugConfig
{
    /// <summary>
    /// "kdnet" or "serial"
    /// </summary>
    public string Transport { get; set; } = "kdnet";
    public KdnetConfig Kdnet { get; set; } = new();
    public SerialConfig Serial { get; set; } = new();
    public string SymbolPath { get; set; } = @"srv*C:\Symbols*https://msdl.microsoft.com/download/symbols";
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
    public int X64DbgAutomatePort { get; set; } = 27043;
}

public sealed class SecurityConfig
{
    public string QuarantineDir { get; set; } = @"C:\MCP_Quarantine";
    public int MaxFileTransferSizeMB { get; set; } = 500;
    public string LogDir { get; set; } = @"C:\MCP_Logs";
}

public sealed class TimeoutConfig
{
    // VM operations (seconds)
    public int VmStartSeconds { get; set; } = 60;
    public int VmStopSeconds { get; set; } = 30;
    public int VmPauseResumeSeconds { get; set; } = 10;
    public int VmSnapshotCreateSeconds { get; set; } = 120;
    public int VmSnapshotRestoreSeconds { get; set; } = 60;
    public int VmScreenshotSeconds { get; set; } = 10;
    public int VmToolsCheckSeconds { get; set; } = 5;
    public int VmGetIpSeconds { get; set; } = 10;

    // Kernel debug operations (seconds)
    public int KdConnectSeconds { get; set; } = 30;
    public int KdInitialBreakSeconds { get; set; } = 15;
    public int KdBreakSeconds { get; set; } = 10;
    public int KdStepSeconds { get; set; } = 10;
    public int KdCommandExecuteSeconds { get; set; } = 30;
    public int KdMemoryReadSeconds { get; set; } = 10;
    public int KdMemoryWriteSeconds { get; set; } = 10;
    public int KdWaitForBreakpointSeconds { get; set; } = 10;

    // Guest operations (seconds)
    public int GuestCommandSeconds { get; set; } = 60;
    public int GuestFileTransferSeconds { get; set; } = 120;
    public int GuestListProcessesSeconds { get; set; } = 15;
    public int GuestKillProcessSeconds { get; set; } = 10;

    // User-mode debug (seconds)
    public int FridaAttachSeconds { get; set; } = 15;
    public int FridaScriptSeconds { get; set; } = 30;
    public int DbgsrvConnectSeconds { get; set; } = 15;
    public int TtdRecordMinutes { get; set; } = 5;
}
