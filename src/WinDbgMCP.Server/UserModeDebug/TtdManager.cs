using Microsoft.Extensions.Logging;
using WinDbgMCP.Server.Guest;

namespace WinDbgMCP.Server.UserModeDebug;

/// <summary>
/// Manages Time Travel Debugging (TTD) recordings.
/// TTD.exe runs inside the guest VM to record process execution.
/// Traces can then be copied to the host for analysis with WinDbg/DbgEng.
/// Requires: TTD.exe installed in the guest VM (e.g., C:\Tools\TTD\TTD.exe).
/// </summary>
public sealed class TtdManager
{
    private readonly GuestExecManager _guest;
    private readonly ILogger<TtdManager> _logger;

    // Default path to TTD.exe in the guest
    private const string DefaultTtdPath = @"C:\Tools\TTD\TTD.exe";

    public TtdManager(GuestExecManager guest, ILogger<TtdManager> logger)
    {
        _guest = guest;
        _logger = logger;
    }

    /// <summary>
    /// Start recording a new process under TTD.
    /// </summary>
    public async Task<string> RecordLaunchAsync(
        string targetPath, string arguments = "",
        string? outputDir = null, int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        outputDir ??= @"C:\TTD_Traces";

        _logger.LogInformation("TTD record launch: {Target} {Args}", targetPath, arguments);

        // Ensure output directory exists
        await _guest.RunCommandAsync($"mkdir \"{outputDir}\" 2>nul", timeoutSeconds: 5, ct: ct);

        // Launch the target under TTD
        var cmd = $"\"{DefaultTtdPath}\" -accepteula -launch \"{targetPath}\" {arguments} -out \"{outputDir}\"";
        var result = await _guest.RunCommandAsync(cmd, timeoutSeconds: timeoutSeconds, ct: ct);

        if (result.Success && result.ExitCode == 0)
        {
            return $"TTD recording completed.\n{result.Stdout}\n" +
                   $"Trace saved to guest:{outputDir}. " +
                   "Use umd_ttd with action='retrieve' to copy the trace to the host, " +
                   "then umd_ttd_query to analyze it.";
        }

        return $"TTD recording failed (exit code {result.ExitCode}).\n" +
               $"stdout: {result.Stdout}\nstderr: {result.Stderr}\n" +
               $"Ensure TTD.exe exists at {DefaultTtdPath} in the guest VM.";
    }

    /// <summary>
    /// Attach TTD to an existing process by PID.
    /// </summary>
    public async Task<string> RecordAttachAsync(
        uint pid, string? outputDir = null, int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        outputDir ??= @"C:\TTD_Traces";

        _logger.LogInformation("TTD record attach to PID {Pid}", pid);

        await _guest.RunCommandAsync($"mkdir \"{outputDir}\" 2>nul", timeoutSeconds: 5, ct: ct);

        var cmd = $"\"{DefaultTtdPath}\" -accepteula -attach {pid} -out \"{outputDir}\"";
        var result = await _guest.RunCommandAsync(cmd, timeoutSeconds: timeoutSeconds, ct: ct);

        if (result.Success && result.ExitCode == 0)
        {
            return $"TTD recording of PID {pid} completed.\n{result.Stdout}\n" +
                   $"Trace saved to guest:{outputDir}.";
        }

        return $"TTD recording failed (exit code {result.ExitCode}).\n" +
               $"stdout: {result.Stdout}\nstderr: {result.Stderr}";
    }

    /// <summary>
    /// Stop an active TTD recording (kills the TTD process).
    /// </summary>
    public async Task<string> StopRecordingAsync(CancellationToken ct = default)
    {
        var result = await _guest.RunCommandAsync(
            "taskkill /f /im TTD.exe", timeoutSeconds: 10, ct: ct);

        return result.Success
            ? "TTD recording stopped."
            : $"Failed to stop TTD: {result.Stderr}";
    }

    /// <summary>
    /// Retrieve a TTD trace file from the guest to the host.
    /// </summary>
    public async Task<string> RetrieveTraceAsync(
        string guestTracePath, string hostOutputPath, CancellationToken ct = default)
    {
        return await _guest.CopyFileFromGuestAsync(guestTracePath, hostOutputPath, ct);
    }

    /// <summary>
    /// List TTD trace files in the guest output directory.
    /// </summary>
    public async Task<string> ListTracesAsync(
        string? outputDir = null, CancellationToken ct = default)
    {
        outputDir ??= @"C:\TTD_Traces";
        var result = await _guest.RunCommandAsync(
            $"dir /b /s \"{outputDir}\\*.run\" 2>nul", timeoutSeconds: 10, ct: ct);

        if (result.Success && !string.IsNullOrWhiteSpace(result.Stdout))
            return $"TTD traces in guest:{outputDir}:\n{result.Stdout}";

        return $"No TTD trace files found in guest:{outputDir}.";
    }
}
