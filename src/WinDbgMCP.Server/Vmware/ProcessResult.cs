namespace WinDbgMCP.Server.Vmware;

/// <summary>
/// Result of a vmrun process execution.
/// </summary>
public sealed class ProcessResult
{
    public int ExitCode { get; }
    public string Stdout { get; }
    public string Stderr { get; }
    public bool Success => ExitCode == 0;

    public ProcessResult(int exitCode, string stdout, string stderr)
    {
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
    }
}

/// <summary>
/// Result of a VM operation.
/// </summary>
public sealed class VmResult
{
    public bool Success { get; }
    public string Message { get; }
    public string? ErrorDetail { get; }

    private VmResult(bool success, string message, string? errorDetail = null)
    {
        Success = success;
        Message = message;
        ErrorDetail = errorDetail;
    }

    public static VmResult Ok(string message) => new(true, message);
    public static VmResult Failed(string message, string? detail = null) => new(false, message, detail);
}

/// <summary>
/// Result of a snapshot list operation.
/// </summary>
public sealed class SnapshotListResult
{
    public bool Success { get; }
    public List<string> Snapshots { get; }
    public string? ErrorMessage { get; }

    private SnapshotListResult(bool success, List<string> snapshots, string? error = null)
    {
        Success = success;
        Snapshots = snapshots;
        ErrorMessage = error;
    }

    public static SnapshotListResult Ok(List<string> snapshots) => new(true, snapshots);
    public static SnapshotListResult Failed(string error) => new(false, new List<string>(), error);
}
