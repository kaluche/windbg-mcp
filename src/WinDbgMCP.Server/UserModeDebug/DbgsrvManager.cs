using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using WinDbgMCP.Server.Configuration;

namespace WinDbgMCP.Server.UserModeDebug;

/// <summary>
/// Manages remote user-mode debugging via dbgsrv.exe running in the guest VM.
/// Uses cdb.exe (console debugger) as an EXTERNAL PROCESS for complete isolation
/// from the kernel debugger's in-process DbgEng state.
///
/// Why external process? DbgEng has global per-process state. When a kernel debug
/// session is active (via DebugCreate on one thread), a second DebugCreate on another
/// thread can ConnectProcessServer but WaitForEvent after AttachProcess never completes.
/// Running cdb.exe as a child process gives fully independent DbgEng state.
///
/// Each command runs a fresh cdb.exe invocation:
///   cdb -premote tcp:port=P,server=IP -pv -p PID -c "command; q"
/// This is stateless but reliable — noninvasive attach gives read-only access to
/// memory, modules, threads, and stacks.
/// </summary>
public sealed class DbgsrvManager : IDisposable
{
    private readonly ServerConfig _config;
    private readonly ILogger<DbgsrvManager> _logger;
    private readonly string? _cdbPath;
    private bool _disposed;

    // Connection state (lightweight — just remembers what to connect to)
    private string? _serverAddress;
    private int _serverPort;

    public bool IsConnected => _serverAddress != null;
    public uint? AttachedPid { get; private set; }

    public DbgsrvManager(ServerConfig config, ILogger<DbgsrvManager> logger)
    {
        _config = config;
        _logger = logger;
        _cdbPath = FindCdbPath();

        if (_cdbPath != null)
            _logger.LogInformation("Found cdb.exe at {Path}", _cdbPath);
        else
            _logger.LogWarning("cdb.exe not found — dbgsrv commands will fail");
    }

    /// <summary>
    /// Connect to dbgsrv running in the guest VM.
    /// This just validates connectivity and stores the connection info.
    /// </summary>
    public async Task<string> ConnectAsync(string vmIpAddress, int port = 5064, CancellationToken ct = default)
    {
        if (_cdbPath == null)
            return "cdb.exe not found. Install Windows SDK Debuggers (WDK) to use dbgsrv.";

        if (_serverAddress != null)
            return "Already connected to dbgsrv. Disconnect first.";

        _logger.LogInformation("Connecting to dbgsrv at {Ip}:{Port}", vmIpAddress, port);

        // Validate the connection by running a quick .tlist command
        var result = await RunCdbAsync(vmIpAddress, port, null, ".tlist", 10);

        if (!result.Success)
            return $"ConnectProcessServer failed — cannot reach dbgsrv at {vmIpAddress}:{port}. " +
                   $"Error: {result.Output}. " +
                   "Troubleshoot: call umd_dbgsrv_skill for step-by-step guide.";

        _serverAddress = vmIpAddress;
        _serverPort = port;
        _logger.LogInformation("Validated dbgsrv connection at {Ip}:{Port}", vmIpAddress, port);
        return $"Connected to dbgsrv at {vmIpAddress}:{port}. " +
               "Use umd_dbgsrv_execute with action='attach' to attach to a process.";
    }

    /// <summary>
    /// Attach to a process by PID via the remote process server.
    /// </summary>
    public async Task<string> AttachToProcessAsync(uint pid)
    {
        if (_serverAddress == null)
            return "Not connected to dbgsrv. Call umd_dbgsrv_connect first.";

        // Validate the attach by listing modules
        var result = await RunCdbAsync(_serverAddress, _serverPort, pid, "lm", 15);

        if (!result.Success)
            return $"AttachProcess failed for PID {pid}: {result.Output}";

        AttachedPid = pid;
        return $"Attached to PID {pid} via dbgsrv (noninvasive). " +
               "Use umd_dbgsrv_execute to run debugging commands.";
    }

    /// <summary>
    /// Execute a WinDbg command on the user-mode target.
    /// </summary>
    public async Task<string> ExecuteCommandAsync(string command, int timeoutSeconds = 30)
    {
        if (_serverAddress == null)
            return "Not connected to dbgsrv.";

        if (AttachedPid == null)
            return "Not attached to a process. Use action='attach' first.";

        var result = await RunCdbAsync(_serverAddress, _serverPort, AttachedPid.Value, command, timeoutSeconds);

        if (!result.Success)
            return $"Command failed: {result.Output}";

        return string.IsNullOrWhiteSpace(result.Output) ? "(no output)" : result.Output;
    }

    /// <summary>
    /// Detach from the current process.
    /// </summary>
    public Task<string> DetachAsync()
    {
        AttachedPid = null;
        return Task.FromResult("Detached from user-mode process.");
    }

    /// <summary>
    /// Disconnect from dbgsrv entirely.
    /// </summary>
    public Task<string> DisconnectAsync()
    {
        _serverAddress = null;
        _serverPort = 0;
        AttachedPid = null;
        return Task.FromResult("Disconnected from dbgsrv.");
    }

    /// <summary>
    /// Run a cdb.exe command against the remote process server.
    /// Each invocation is a fresh process — complete isolation from kernel debugger.
    /// </summary>
    private async Task<CdbResult> RunCdbAsync(string server, int port, uint? pid, string command, int timeoutSeconds)
    {
        if (_cdbPath == null)
            return new CdbResult(false, "cdb.exe not found");

        // Build cdb.exe arguments
        var args = new StringBuilder();

        // Connect to remote process server
        args.Append($"-premote tcp:port={port},server={server} ");

        if (pid.HasValue)
        {
            // Noninvasive attach to specific PID (process keeps running)
            args.Append($"-pv -p {pid.Value} ");
        }

        // Run command then quit. Use .echo markers for reliable output parsing.
        // Suppress initial banner noise with -lines (faster startup).
        args.Append($"-c \".echo <<<START>>>; {EscapeCdbCommand(command)}; .echo <<<END>>>; q\"");

        _logger.LogDebug("Running: cdb {Args}", args);

        var psi = new ProcessStartInfo
        {
            FileName = _cdbPath,
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
                return new CdbResult(false, "Failed to start cdb.exe");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new CdbResult(false, $"cdb.exe timed out after {timeoutSeconds}s");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // Parse output between markers
            var output = ExtractOutput(stdout);

            // Check for common error patterns
            if (stdout.Contains("Unable to connect to") || stdout.Contains("could not attach"))
                return new CdbResult(false, output ?? stdout.Trim());

            return new CdbResult(true, output ?? stdout.Trim());
        }
        catch (Exception ex)
        {
            return new CdbResult(false, $"cdb.exe error: {ex.Message}");
        }
    }

    /// <summary>
    /// Extract the output between <<<START>>> and <<<END>>> markers.
    /// Uses LastIndexOf for START because cdb echoes the initial command
    /// (including markers) before executing it. The actual .echo output
    /// appears AFTER the command echo.
    /// </summary>
    private static string? ExtractOutput(string fullOutput)
    {
        const string startMarker = "<<<START>>>";
        const string endMarker = "<<<END>>>";

        // Find the LAST <<<START>>> — the actual .echo output, not the command echo
        var startIdx = fullOutput.LastIndexOf(startMarker, StringComparison.Ordinal);
        if (startIdx < 0) return null;
        startIdx += startMarker.Length;

        var endIdx = fullOutput.IndexOf(endMarker, startIdx, StringComparison.Ordinal);
        if (endIdx < 0) return null;

        var result = fullOutput[startIdx..endIdx].Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    /// <summary>
    /// Escape semicolons in user commands that are part of string literals, not command separators.
    /// CDB uses semicolons as command separators in -c strings.
    /// </summary>
    private static string EscapeCdbCommand(string command)
    {
        // Don't escape — users should be able to chain commands with ;
        // The .echo markers handle output boundaries
        return command;
    }

    private static string? FindCdbPath()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe",
            @"C:\Program Files\Windows Kits\10\Debuggers\x64\cdb.exe",
            @"C:\Debuggers\cdb.exe",
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // No resources to clean up — each cdb.exe invocation is independent
    }

    private record CdbResult(bool Success, string Output);
}
