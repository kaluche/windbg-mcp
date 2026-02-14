using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.Vmware;

namespace WinDbgMCP.Server.UserModeDebug;

/// <summary>
/// Manages Frida instrumentation via the frida CLI tools.
/// Uses frida-tools (Python CLI) as a subprocess for simplicity.
/// Requires: frida-tools installed on host (pip install frida-tools),
///           frida-server.exe running in the guest VM.
/// </summary>
public sealed class FridaManager : IDisposable
{
    private const string NoIpError =
        "Cannot determine guest VM IP address. Is the VM running with VMware Tools?";

    private readonly ServerConfig _config;
    private readonly VmwareManager _vmware;
    private readonly ILogger<FridaManager> _logger;
    private bool _disposed;
    private string? _cachedVmIp;

    // Background session state
    private Process? _backgroundProcess;
    private readonly List<string> _backgroundOutput = new();
    private readonly object _backgroundLock = new();
    private string? _backgroundScriptPath;

    public string? AttachedProcessName { get; private set; }
    public int? AttachedPid { get; private set; }
    public bool IsAttached => AttachedProcessName != null || AttachedPid != null;
    public bool HasBackgroundSession => _backgroundProcess != null && !_backgroundProcess.HasExited;

    public FridaManager(ServerConfig config, VmwareManager vmware, ILogger<FridaManager> logger)
    {
        _config = config;
        _vmware = vmware;
        _logger = logger;
    }

    /// <summary>
    /// Get the VM's IP address for Frida connection.
    /// </summary>
    private async Task<string?> GetFridaHostAsync()
    {
        _cachedVmIp ??= await _vmware.GetGuestIpAddressAsync();
        if (string.IsNullOrEmpty(_cachedVmIp))
            return null;
        return $"{_cachedVmIp}:{_config.Guest.FridaPort}";
    }

    /// <summary>
    /// Attach to a process by PID in the guest VM.
    /// </summary>
    public async Task<string> AttachAsync(int pid, CancellationToken ct = default)
    {
        if (IsAttached)
            return $"Already attached to PID {AttachedPid} ({AttachedProcessName}). " +
                   "Call umd_frida with action='detach' first.";

        var host = await GetFridaHostAsync();
        if (host == null) return NoIpError;
        var result = await RunFridaCommandAsync(
            $"-H {host} -p {pid} -q -t 1 -e \"console.log('Frida attached to PID ' + Process.id)\"",
            TimeSpan.FromSeconds(15), ct);

        if (result.Success)
        {
            AttachedPid = pid;
            AttachedProcessName = $"PID {pid}";
            return $"Frida attached to PID {pid} on {host}. " +
                   "Use umd_frida with action='inject' to run JavaScript instrumentation.";
        }

        return $"Frida attach failed: {result.Output}";
    }

    /// <summary>
    /// Attach to a process by name in the guest VM.
    /// </summary>
    public async Task<string> AttachByNameAsync(string processName, CancellationToken ct = default)
    {
        if (IsAttached)
            return $"Already attached to PID {AttachedPid} ({AttachedProcessName}). " +
                   "Call umd_frida with action='detach' first.";

        var host = await GetFridaHostAsync();
        if (host == null) return NoIpError;
        var result = await RunFridaCommandAsync(
            $"-H {host} -n \"{processName}\" -q -t 1 -e \"console.log('Frida attached to ' + Process.id)\"",
            TimeSpan.FromSeconds(15), ct);

        if (result.Success)
        {
            AttachedProcessName = processName;
            return $"Frida attached to '{processName}' on {host}. " +
                   "Use umd_frida with action='inject' to run JavaScript instrumentation.";
        }

        return $"Frida attach failed: {result.Output}";
    }

    /// <summary>
    /// Inject a JavaScript script into the attached process.
    /// When eternalize is true, the script persists in the target process after
    /// the frida CLI exits — hooks (Interceptor.attach/replace) survive across sessions.
    /// </summary>
    public async Task<string> InjectScriptAsync(string jsCode, int timeoutSeconds = 30,
        bool eternalize = false, CancellationToken ct = default)
    {
        if (!IsAttached)
            return "Not attached to any process. Call umd_frida_attach first.";

        // Write script to temp file
        var scriptPath = Path.Combine(Path.GetTempPath(), $"frida_script_{Guid.NewGuid():N}.js");
        try
        {
            await File.WriteAllTextAsync(scriptPath, jsCode, ct);

            var host = await GetFridaHostAsync();
            if (host == null) return NoIpError;
            var pidArg = AttachedPid.HasValue ? $"-p {AttachedPid}" : $"-n \"{AttachedProcessName}\"";
            var eternalizeFlag = eternalize ? " --eternalize" : "";
            var result = await RunFridaCommandAsync(
                $"-H {host} {pidArg} -q -t {timeoutSeconds}{eternalizeFlag} -l \"{scriptPath}\"",
                TimeSpan.FromSeconds(timeoutSeconds + 5), ct);

            var prefix = eternalize
                ? "Script injected and eternalized (hooks persist after this call).\n"
                : "Script injected successfully.\n";

            return result.Success
                ? $"{prefix}{result.Output}"
                : $"Script injection failed: {result.Output}";
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>
    /// Run a one-liner Frida command.
    /// </summary>
    public async Task<string> EvalAsync(string jsExpression, int timeoutSeconds = 15, CancellationToken ct = default)
    {
        if (!IsAttached)
            return "Not attached to any process. Call umd_frida_attach first.";

        var host = await GetFridaHostAsync();
        if (host == null) return NoIpError;
        var pidArg = AttachedPid.HasValue ? $"-p {AttachedPid}" : $"-n \"{AttachedProcessName}\"";
        var result = await RunFridaCommandAsync(
            $"-H {host} {pidArg} -q -t 1 -e \"{EscapeJs(jsExpression)}\"",
            TimeSpan.FromSeconds(timeoutSeconds), ct);

        return result.Success ? result.Output : $"Eval failed: {result.Output}";
    }

    /// <summary>
    /// List processes via frida-ps.
    /// </summary>
    public async Task<string> ListProcessesAsync(CancellationToken ct = default)
    {
        var host = await GetFridaHostAsync();
        if (host == null) return NoIpError;
        var result = await RunFridaToolAsync("frida-ps",
            $"-H {host}", TimeSpan.FromSeconds(10), ct);

        return result.Success ? result.Output : $"frida-ps failed: {result.Output}";
    }

    /// <summary>
    /// Start a background frida session that keeps hooks alive indefinitely.
    /// The frida process runs with -t inf. Use CollectBackgroundOutput() to
    /// read captured output and StopBackgroundSession() to terminate.
    /// </summary>
    public async Task<string> InjectBackgroundAsync(string jsCode, CancellationToken ct = default)
    {
        if (!IsAttached)
            return "Not attached to any process. Call umd_frida_attach first.";

        if (HasBackgroundSession)
            return "A background session is already running. Call umd_frida with action='stop_bg' first.";

        var host = await GetFridaHostAsync();
        if (host == null) return NoIpError;

        var scriptPath = Path.Combine(Path.GetTempPath(), $"frida_bg_{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(scriptPath, jsCode, ct);
        _backgroundScriptPath = scriptPath;

        var resolvedTool = ResolveToolPath("frida");
        var pidArg = AttachedPid.HasValue ? $"-p {AttachedPid}" : $"-n \"{AttachedProcessName}\"";
        var psi = new ProcessStartInfo
        {
            FileName = resolvedTool,
            Arguments = $"-H {host} {pidArg} -q -t inf -l \"{scriptPath}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _logger.LogDebug("Starting background frida session: {Args}", psi.Arguments);

        var process = Process.Start(psi);
        if (process == null)
        {
            CleanupBackgroundScriptFile();
            return "Failed to start frida. Is frida-tools installed?";
        }

        process.StandardInput.Close();
        _backgroundProcess = process;

        lock (_backgroundLock)
        {
            _backgroundOutput.Clear();
        }

        // Read stdout/stderr asynchronously in background
        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.StandardOutput.EndOfStream)
                {
                    var line = await process.StandardOutput.ReadLineAsync();
                    if (line != null)
                        lock (_backgroundLock) { _backgroundOutput.Add(line); }
                }
            }
            catch { /* process exited */ }
        }, CancellationToken.None);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.StandardError.EndOfStream)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (line != null)
                        lock (_backgroundLock) { _backgroundOutput.Add(line); }
                }
            }
            catch { /* process exited */ }
        }, CancellationToken.None);

        // Wait briefly to check for immediate errors
        await Task.Delay(1500, ct);

        if (process.HasExited)
        {
            string output;
            lock (_backgroundLock) { output = string.Join("\n", _backgroundOutput); }
            CleanupBackgroundSession();
            return $"Background session failed to start: {output}";
        }

        string earlyOutput;
        lock (_backgroundLock) { earlyOutput = string.Join("\n", _backgroundOutput); }

        var msg = $"Background frida session started. Hooks are active and will persist until stopped.\n" +
                  $"Use umd_frida(action='collect_bg') to read output.\n" +
                  $"Use umd_frida(action='stop_bg') to stop.";
        if (!string.IsNullOrEmpty(earlyOutput))
            msg += $"\n\nInitial output:\n{earlyOutput}";

        return msg;
    }

    /// <summary>
    /// Collect output from the background frida session.
    /// </summary>
    public string CollectBackgroundOutput()
    {
        if (!HasBackgroundSession)
            return "No background session running.";

        string output;
        lock (_backgroundLock)
        {
            output = string.Join("\n", _backgroundOutput);
            _backgroundOutput.Clear();
        }

        return string.IsNullOrEmpty(output)
            ? "(no new output)"
            : output;
    }

    /// <summary>
    /// Stop the background frida session.
    /// </summary>
    public string StopBackgroundSession()
    {
        if (_backgroundProcess == null)
            return "No background session to stop.";

        string finalOutput;
        lock (_backgroundLock)
        {
            finalOutput = string.Join("\n", _backgroundOutput);
            _backgroundOutput.Clear();
        }

        try
        {
            if (!_backgroundProcess.HasExited)
                _backgroundProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error killing background frida process");
        }

        CleanupBackgroundSession();

        var msg = "Background session stopped. Hooks have been removed.";
        if (!string.IsNullOrEmpty(finalOutput))
            msg += $"\n\nFinal output:\n{finalOutput}";
        return msg;
    }

    private void CleanupBackgroundSession()
    {
        try { _backgroundProcess?.Dispose(); } catch { }
        _backgroundProcess = null;
        CleanupBackgroundScriptFile();
    }

    private void CleanupBackgroundScriptFile()
    {
        if (_backgroundScriptPath != null)
        {
            try { File.Delete(_backgroundScriptPath); } catch { }
            _backgroundScriptPath = null;
        }
    }

    /// <summary>
    /// Detach from the current process. Also stops any background session.
    /// </summary>
    public string Detach()
    {
        if (!IsAttached)
            return "Not attached to any process.";

        // Stop background session if active
        if (HasBackgroundSession)
            StopBackgroundSession();

        var name = AttachedProcessName ?? $"PID {AttachedPid}";
        AttachedPid = null;
        AttachedProcessName = null;
        return $"Detached from {name}.";
    }

    private async Task<FridaResult> RunFridaCommandAsync(
        string arguments, TimeSpan timeout, CancellationToken ct)
    {
        return await RunFridaToolAsync("frida", arguments, timeout, ct);
    }

    private async Task<FridaResult> RunFridaToolAsync(
        string tool, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            // Resolve full path to avoid PATH issues when spawned under MCP stdio transport
            var resolvedTool = ResolveToolPath(tool);
            var psi = new ProcessStartInfo
            {
                FileName = resolvedTool,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true, // Prevent inheriting MCP stdin
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            _logger.LogDebug("Running {Tool} {Args}", tool, arguments);

            using var process = Process.Start(psi);
            if (process == null)
                return new FridaResult(false, $"Failed to start {tool}. Is frida-tools installed? (pip install frida-tools)");

            // Close stdin immediately so frida doesn't wait for interactive input
            process.StandardInput.Close();

            // Use ReadToEndAsync instead of event-based BeginOutputReadLine.
            // WaitForExitAsync can return before event handlers fire, causing empty output.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }

            string output, error;
            try
            {
                output = (await stdoutTask).Trim();
                error = (await stderrTask).Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read {Tool} output streams", tool);
                output = "";
                error = "";
            }

            var exitCode = -1;
            try { exitCode = process.ExitCode; } catch { }

            _logger.LogDebug("{Tool} exit={ExitCode} timedOut={TimedOut} stdout=[{Stdout}] stderr=[{Stderr}]",
                tool, exitCode, timedOut, output, error);

            // Frida sends most output to stderr. Combine both streams.
            var combined = string.IsNullOrEmpty(output)
                ? error
                : string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            if (exitCode == 0)
                return new FridaResult(true, string.IsNullOrEmpty(combined) ? "(no output)" : combined);

            // frida in quiet mode (-q) exits with code 1 even on success.
            // If we got meaningful output and no error keywords, treat as success.
            if (exitCode == 1 && !string.IsNullOrEmpty(combined)
                && !combined.Contains("Failed to") && !combined.Contains("Error:")
                && !combined.Contains("unable to"))
                return new FridaResult(true, combined);

            if (timedOut)
                return new FridaResult(false, $"Timed out after {timeout.TotalSeconds}s. Output: {combined}");

            return new FridaResult(false,
                $"Exit code {exitCode}. Output: {(string.IsNullOrEmpty(combined) ? "(empty)" : combined)}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start {Tool}", tool);
            return new FridaResult(false,
                $"'{tool}' not found. Install frida-tools: pip install frida-tools");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error running {Tool}", tool);
            return new FridaResult(false, $"Unexpected error: {ex.Message}");
        }
    }

    private static string EscapeJs(string js)
    {
        return js.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Resolve a tool name to its full path. When the MCP server is spawned
    /// via stdio transport, PATH resolution may differ from the user's shell.
    /// </summary>
    private string ResolveToolPath(string tool)
    {
        // If already an absolute path, use it
        if (Path.IsPathRooted(tool)) return tool;

        // Try common Python Scripts locations
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python312", "Scripts", $"{tool}.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python311", "Scripts", $"{tool}.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python310", "Scripts", $"{tool}.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "Scripts", $"{tool}.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _logger.LogDebug("Resolved {Tool} to {Path}", tool, candidate);
                return candidate;
            }
        }

        // Fallback: rely on PATH
        return tool;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (HasBackgroundSession)
            StopBackgroundSession();
        AttachedPid = null;
        AttachedProcessName = null;
    }
}

public sealed class FridaResult
{
    public bool Success { get; }
    public string Output { get; }

    public FridaResult(bool success, string output)
    {
        Success = success;
        Output = output;
    }
}
