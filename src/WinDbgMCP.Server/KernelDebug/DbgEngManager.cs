using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ClrDebug;
using ClrDebug.DbgEng;
using Microsoft.Extensions.Logging;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.KernelDebug.Interop;
using WinDbgMCP.Server.KernelDebug.Models;
using WinDbgMCP.Server.State;

namespace WinDbgMCP.Server.KernelDebug;

/// <summary>
/// Manages the DbgEng kernel debugging session.
/// All DbgEng COM operations are marshaled to the dedicated DbgEngThread.
/// </summary>
public sealed class DbgEngManager : IDisposable
{
    private readonly DbgEngThread _thread;
    private readonly ServerConfig _config;
    private readonly ILogger<DbgEngManager> _logger;
    private readonly OutputCapture _outputCapture = new();
    private readonly DebugEventCallbacks _eventCallbacks = new();

    private DebugClient? _client;
    private DbgEngInterruptor? _interruptor;
    private bool _isAttached;
    private KdTransport _transport = KdTransport.None;
    private int _explicitBreakInterruptPending;
    private int _pumpWaitActive;
    private int _nonBreakingPumpWakePending;
    private bool _disposed;

    public bool IsConnected => _client != null && _isAttached;
    public KdTransport CurrentTransport => IsConnected ? _transport : KdTransport.None;
    public int PendingEventCount => _eventCallbacks.PendingCount;

    public DbgEngManager(DbgEngThread thread, ServerConfig config, ILogger<DbgEngManager> logger)
    {
        _thread = thread;
        _config = config;
        _logger = logger;

        // Set up the event pump action
        _thread.PumpEventsAction = PumpEvents;
        _thread.PumpArmingAction = ArmPumpWait;
        _thread.PumpDisarmedAction = DisarmPumpWait;
        _thread.WorkQueuedWhilePumpingAction = WakePumpForQueuedWork;
    }

    // ═══════════════════════════════════════════════════════════════
    //  CONNECTION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Connect to a kernel debug target via KDNET or serial.
    /// </summary>
    public async Task<string> ConnectKernelAsync(string? connectionString = null, CancellationToken ct = default)
    {
        var timeout = GetConnectOperationTimeout(_config.Timeouts);

        return await _thread.ExecuteAsync(() =>
        {
            // Tear down any client left over from a prior failed/aborted connect so we
            // don't leak it (and its bound KDNET UDP port) when creating a new one —
            // a leaked client keeps the port bound and makes retries fail with E_FAIL.
            if (_client != null)
            {
                _interruptor?.Dispose();
                _interruptor = null;
                try { _client.TryEndSession(DEBUG_END.ACTIVE_TERMINATE); } catch { }
                _client = null;
                _isAttached = false;
                _transport = KdTransport.None;
                Volatile.Write(ref _pumpWaitActive, 0);
                Volatile.Write(ref _nonBreakingPumpWakePending, 0);
            }
            _eventCallbacks.ClearEvents();

            _logger.LogInformation("Creating DbgEng client...");

            // Find Windows SDK debugger directory for dbgeng.dll
            var debuggerDir = FindDebuggerDirectory();
            if (debuggerDir != null)
            {
                NativeMethods.SetDllDirectory(debuggerDir);
                _logger.LogInformation("Using dbgeng from: {Dir}", debuggerDir);
            }

            // Load dbgeng.dll and get DebugCreate
            var hDbgEng = NativeMethods.LoadLibrary("dbgeng.dll");
            if (hDbgEng == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Failed to load dbgeng.dll. Install Debugging Tools for Windows " +
                    "(part of Windows SDK) or WinDbg Preview from the Microsoft Store.");

            var pDebugCreate = NativeMethods.GetProcAddress(hDbgEng, "DebugCreate");
            if (pDebugCreate == IntPtr.Zero)
                throw new InvalidOperationException("Failed to find DebugCreate in dbgeng.dll");

            var debugCreate = Marshal.GetDelegateForFunctionPointer<Interop.DebugCreateDelegate>(pDebugCreate);

            // Create the debug client
            var hr = debugCreate(DebugClient.IID_IDebugClient, out var pClient);
            if (hr != HRESULT.S_OK)
                throw new InvalidOperationException($"DebugCreate failed: {hr}");

            _client = new DebugClient(pClient);

            // Set callbacks
            _client.OutputCallbacks = _outputCapture;
            _client.EventCallbacks = _eventCallbacks;

            // Configure engine options
            _client.Control.EngineOptions = DEBUG_ENGOPT.INITIAL_BREAK;

            // Configure symbol path
            _client.Symbols.SymbolPath = _config.KernelDebug.SymbolPath;
            _logger.LogInformation("Symbol path: {Path}", _config.KernelDebug.SymbolPath);

            // Build connection string
            string connStr;
            KdTransport transport;
            if (connectionString != null)
            {
                connStr = connectionString;
                transport = connectionString.StartsWith("net:", StringComparison.OrdinalIgnoreCase)
                    ? KdTransport.KDNET
                    : KdTransport.Serial;
            }
            else if (_config.KernelDebug.Transport.Equals("kdnet", StringComparison.OrdinalIgnoreCase))
            {
                connStr = $"net:port={_config.KernelDebug.Kdnet.Port},key={_config.KernelDebug.Kdnet.Key}";
                transport = KdTransport.KDNET;
            }
            else if (_config.KernelDebug.Transport.Equals("serial", StringComparison.OrdinalIgnoreCase))
            {
                connStr = $"com:pipe,port={_config.KernelDebug.Serial.PipeName},resets=0,reconnect";
                transport = KdTransport.Serial;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unknown kernel debug transport: '{_config.KernelDebug.Transport}'. " +
                    "Use 'kdnet' or 'serial'.");
            }

            _logger.LogInformation("Attaching kernel: {ConnStr}", connStr);

            // Attach to kernel
            var attachHr = _client.TryAttachKernel(DEBUG_ATTACH.KERNEL_CONNECTION, connStr);
            if (attachHr != HRESULT.S_OK)
            {
                // Clean up the failed client so it doesn't leak the KDNET UDP socket.
                try { _client.TryEndSession(DEBUG_END.ACTIVE_TERMINATE); } catch { }
                _client = null;
                _isAttached = false;
                _transport = KdTransport.None;
                Volatile.Write(ref _pumpWaitActive, 0);
                Volatile.Write(ref _nonBreakingPumpWakePending, 0);
                _eventCallbacks.ClearEvents();
                throw new InvalidOperationException(
                    $"AttachKernel failed: {attachHr}. " + ErrorMessages.KdConnectFailed);
            }

            _isAttached = true;
            _transport = transport;
            _interruptor = new DbgEngInterruptor(_client, _logger);

            _logger.LogInformation("AttachKernel succeeded, waiting for initial breakpoint...");

            // WaitForEvent for live kernel targets MUST use INFINITE timeout
            // (per Microsoft docs). Use DEBUG_INTERRUPT_EXIT from a timer as
            // the safety net so the wait is cancelled without breaking the target.
            var waitTimeoutMs = _config.Timeouts.KdInitialBreakSeconds * 1000;
            using var interruptTimer = CreateInterruptTimer(
                DbgEngInterruptPurpose.ConnectInitialBreakTimeout,
                waitTimeoutMs);

            var waitHr = _client.Control.TryWaitForEvent(
                DEBUG_WAIT.DEFAULT,
                DbgEngEventHandling.InfiniteWaitMilliseconds);
            interruptTimer.Dispose();

            if (waitHr == HRESULT.S_OK)
            {
                _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.BREAK);
                _logger.LogInformation("Connected. Target at initial breakpoint.");

                // Force reload symbols
                _outputCapture.Clear();
                _client.Control.TryExecute(DEBUG_OUTCTL.THIS_CLIENT, ".reload /f", DEBUG_EXECUTE.DEFAULT);
                _outputCapture.GetAndClear(); // Discard reload output

                return $"Connected to kernel via {transport}. Target is at initial breakpoint. " +
                       "You can now use kd_execute to run WinDbg commands, or kd_continue to resume.";
            }
            else if (DbgEngEventHandling.IsNormalNonEventWaitResult(waitHr))
            {
                // Timeout/exit interrupt — target is running but we're connected
                _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.GO);
                _logger.LogInformation("Connected. Target is running (no initial break within timeout).");
                SetPumpEnabledForRunningTarget();

                return $"Connected to kernel via {transport}. Target is running freely. " +
                       "Call kd_break to halt the target for inspection.";
            }
            else
            {
                // Real failure
                _client.TryEndSession(DEBUG_END.ACTIVE_TERMINATE);
                _interruptor?.Dispose();
                _interruptor = null;
                _client = null;
                _isAttached = false;
                _transport = KdTransport.None;
                Volatile.Write(ref _pumpWaitActive, 0);
                Volatile.Write(ref _nonBreakingPumpWakePending, 0);
                _eventCallbacks.ClearEvents();
                throw new InvalidOperationException(
                    $"WaitForEvent failed: {DbgEngEventHandling.FormatHResult(waitHr)}. " +
                    ErrorMessages.KdConnectFailed);
            }
        }, timeout);
    }

    /// <summary>
    /// Disconnect from the kernel debug target.
    /// Resumes the target first so the VM keeps running after disconnect.
    /// </summary>
    public async Task<string> DisconnectAsync()
    {
        if (_client == null)
            return "Not connected.";

        // A running target normally has the background pump blocked inside
        // WaitForEvent(INFINITE). Wake it immediately before queuing disconnect
        // work; otherwise the work item can time out behind the pump. This is
        // an explicit detach operation, so ACTIVE is acceptable here to regain
        // control before resuming and detaching.
        var wasPumping = _thread.PumpEnabled;
        _thread.PumpEnabled = false;
        if (wasPumping)
            QueueInterrupt(DbgEngInterruptPurpose.DisconnectPumpWake);

        return await _thread.ExecuteAsync(() =>
        {
            _thread.PumpEnabled = false;

            if (_client == null)
                return "Not connected.";

            try
            {
                var status = _client.Control.ExecutionStatus;
                if (status == DEBUG_STATUS.BREAK)
                {
                    // ACTIVE_DETACH disconnects from the target, but request GO first
                    // so a live target is not intentionally left frozen while detaching.
                    var resumeHr = _client.Control.TrySetExecutionStatus(
                        DbgEngEventHandling.GetContinueExecutionStatus());
                    if (resumeHr == HRESULT.S_OK)
                        _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.GO);
                    else
                        _logger.LogDebug(
                            "Ignoring failed resume before detach: {HResult}",
                            DbgEngEventHandling.FormatHResult(resumeHr));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring failure while checking/resuming target before detach.");
            }

            try
            {
                _client?.TryEndSession(DEBUG_END.ACTIVE_DETACH);
            }
            catch { }

            _interruptor?.Dispose();
            _interruptor = null;
            _client = null;
            _isAttached = false;
            _transport = KdTransport.None;
            Volatile.Write(ref _pumpWaitActive, 0);
            Volatile.Write(ref _nonBreakingPumpWakePending, 0);
            _eventCallbacks.ClearEvents();
            _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.NO_DEBUGGEE);
            _logger.LogInformation("Disconnected from kernel debugger.");
            return "Disconnected from kernel debugger. Target has been resumed.";
        }, TimeSpan.FromSeconds(30));
    }

    // ═══════════════════════════════════════════════════════════════
    //  STATE QUERY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Get the current execution status. Safe to call from any thread
    /// via the DbgEngThread.
    /// </summary>
    public DebugExecutionStatus GetExecutionStatus()
    {
        if (_client == null) return DebugExecutionStatus.NoDebuggee;

        try
        {
            // Use the cached value from event callbacks (thread-safe)
            var status = _eventCallbacks.LastExecutionStatus;
            return (DebugExecutionStatus)(int)status;
        }
        catch
        {
            return DebugExecutionStatus.Uninitialized;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  COMMAND EXECUTION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Execute a WinDbg command and capture its output.
    /// Must be called while target is in Break state.
    /// </summary>
    public async Task<string> ExecuteCommandAsync(string command, int timeoutSeconds = 30)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                throw new InvalidOperationException("Not connected.");

            _outputCapture.Clear();

            var hr = _client.Control.TryExecute(
                DEBUG_OUTCTL.THIS_CLIENT,
                command,
                DEBUG_EXECUTE.DEFAULT);

            var output = _outputCapture.GetAndClear();

            if (hr != HRESULT.S_OK && hr != HRESULT.S_FALSE)
                return $"Command failed (0x{(int)hr:X8}): {output}";

            return string.IsNullOrWhiteSpace(output) ? "(no output)" : output;
        }, timeout);
    }

    // ═══════════════════════════════════════════════════════════════
    //  EXECUTION CONTROL
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Break into a running target.
    /// </summary>
    public async Task<string> BreakAsync()
    {
        var timeout = GetBreakOperationTimeout(_config.Timeouts);

        // If the background pump is currently in WaitForEvent(INFINITE), a
        // user-requested break must not sit behind the pump wait.
        // ACTIVE is intentional here: this is the explicit kd_break workflow.
        var wasPumping = _thread.PumpEnabled;
        _thread.PumpEnabled = false;
        Volatile.Write(ref _explicitBreakInterruptPending, 1);
        if (wasPumping)
            QueueInterrupt(DbgEngInterruptPurpose.ExplicitTargetBreak);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                throw new InvalidOperationException("Not connected.");

            _thread.PumpEnabled = false;

            if (_client.Control.ExecutionStatus == DEBUG_STATUS.BREAK)
            {
                Volatile.Write(ref _explicitBreakInterruptPending, 0);
                _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.BREAK);
                return FormatTargetHaltedMessage();
            }

            _client.Control.SetInterrupt(
                DbgEngEventHandling.GetInterrupt(DbgEngInterruptPurpose.ExplicitTargetBreak));

            // Wait for the break to take effect (INFINITE + interrupt timer for kernel targets)
            var breakTimeoutMs = _config.Timeouts.KdBreakSeconds * 1000;
            using var interruptTimer = CreateInterruptTimer(
                DbgEngInterruptPurpose.BreakWaitTimeout,
                breakTimeoutMs);

            var waitHr = _client.Control.TryWaitForEvent(
                DEBUG_WAIT.DEFAULT,
                DbgEngEventHandling.InfiniteWaitMilliseconds);
            interruptTimer.Dispose();

            if (waitHr == HRESULT.S_OK)
            {
                Volatile.Write(ref _explicitBreakInterruptPending, 0);
                _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.BREAK);
                return FormatTargetHaltedMessage();
            }

            if (DbgEngEventHandling.IsNormalNonEventWaitResult(waitHr))
            {
                Volatile.Write(ref _explicitBreakInterruptPending, 0);
                _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.GO);
                SetPumpEnabledForRunningTarget();
                return "SetInterrupt sent but target did not break within timeout. " +
                       "The target may be in a non-interruptible state. Try again or check get_system_state.";
            }

            throw new InvalidOperationException(
                $"WaitForEvent after SetInterrupt failed: {DbgEngEventHandling.FormatHResult(waitHr)}");
        }, timeout);
    }

    /// <summary>
    /// Resume execution (go). Returns immediately.
    /// </summary>
    public async Task<string> ContinueAsync()
    {
        var timeout = TimeSpan.FromSeconds(5);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                throw new InvalidOperationException("Not connected.");

            _eventCallbacks.ClearBreakingEventFlag();
            _eventCallbacks.ClearEvents();
            var hr = _client.Control.TrySetExecutionStatus(
                DbgEngEventHandling.GetContinueExecutionStatus());
            if (hr != HRESULT.S_OK)
                throw new InvalidOperationException(
                    $"SetExecutionStatus(GO_HANDLED) failed: {DbgEngEventHandling.FormatHResult(hr)}");

            _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.GO);
            SetPumpEnabledForRunningTarget();

            return "Target resumed. Guest operations are now available. " +
                   "If you set breakpoints, call kd_wait_for_event to check for hits, " +
                   "or call kd_break to halt the target manually.";
        }, timeout);
    }

    /// <summary>
    /// Step one instruction (into or over).
    /// </summary>
    public async Task<string> StepAsync(string mode = "over")
    {
        var timeout = TimeSpan.FromSeconds(_config.Timeouts.KdStepSeconds);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                throw new InvalidOperationException("Not connected.");

            var status = mode.ToLowerInvariant() switch
            {
                "into" => DEBUG_STATUS.STEP_INTO,
                "over" => DEBUG_STATUS.STEP_OVER,
                _ => throw new ArgumentException(
                    $"Invalid step mode '{mode}'. Use 'into' or 'over'.")
            };

            _client.Control.TrySetExecutionStatus(status);

            // Wait for step to complete (INFINITE + interrupt timer for kernel targets)
            var stepTimeoutMs = _config.Timeouts.KdStepSeconds * 1000;
            using var interruptTimer = CreateInterruptTimer(
                DbgEngInterruptPurpose.StepTimeout,
                stepTimeoutMs);

            var waitHr = _client.Control.TryWaitForEvent(
                DEBUG_WAIT.DEFAULT,
                DbgEngEventHandling.InfiniteWaitMilliseconds);
            interruptTimer.Dispose();

            if (waitHr == HRESULT.S_OK)
            {
                _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.BREAK);
                // Show where we ended up
                _outputCapture.Clear();
                _client.Control.TryExecute(
                    DEBUG_OUTCTL.THIS_CLIENT, "r rip", DEBUG_EXECUTE.DEFAULT);
                var rip = _outputCapture.GetAndClear().Trim();

                _outputCapture.Clear();
                _client.Control.TryExecute(
                    DEBUG_OUTCTL.THIS_CLIENT, "u . L1", DEBUG_EXECUTE.DEFAULT);
                var disasm = _outputCapture.GetAndClear().Trim();

                return $"Step {mode} complete.\n{rip}\n{disasm}";
            }

            if (DbgEngEventHandling.IsNormalNonEventWaitResult(waitHr))
            {
                _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.GO);
                SetPumpEnabledForRunningTarget();
                return $"Step {mode} timed out. The instruction may have caused a long-running " +
                       "operation. Call kd_break to interrupt, or kd_wait_for_event to continue waiting.";
            }

            throw new InvalidOperationException(
                $"WaitForEvent after step failed: {DbgEngEventHandling.FormatHResult(waitHr)}");
        }, timeout + TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Wait for a debug event (breakpoint hit, exception, etc.).
    /// </summary>
    public async Task<string> WaitForEventAsync(int timeoutSeconds = 10)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected.");

        if (_eventCallbacks.LastExecutionStatus == DEBUG_STATUS.BREAK ||
            _eventCallbacks.HasBreakingEvent)
        {
            return await FormatCurrentDebugEventAsync();
        }

        _eventCallbacks.ClearBreakingEventFlag();
        SetPumpEnabledForRunningTarget();

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (_client == null)
                throw new InvalidOperationException("Not connected.");

            if (_eventCallbacks.LastExecutionStatus == DEBUG_STATUS.BREAK ||
                _eventCallbacks.HasBreakingEvent)
            {
                return await FormatCurrentDebugEventAsync();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.GO);
        SetPumpEnabledForRunningTarget();
        return $"No debug event received within {timeoutSeconds}s. Target is still running. " +
               "You can: (1) Call kd_wait_for_event again to keep waiting, " +
               "(2) Call kd_break to manually halt the target, or " +
               "(3) Proceed with guest operations while the target runs.";
    }

    // ═══════════════════════════════════════════════════════════════
    //  BSOD DETECTION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Check if the current break is due to a BSOD/bugcheck.
    /// Must be called while target is broken in.
    /// </summary>
    public async Task<(bool IsBugcheck, string? BugcheckCode)> DetectBugcheckAsync()
    {
        var timeout = TimeSpan.FromSeconds(5);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                return (false, (string?)null);

            _outputCapture.Clear();
            _client.Control.TryExecute(
                DEBUG_OUTCTL.THIS_CLIENT, ".lastevent", DEBUG_EXECUTE.DEFAULT);
            var output = _outputCapture.GetAndClear();

            if (output.Contains("bugcheck", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Bug Check", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(output, @"[Bb]ug\s*[Cc]heck\s+([\dA-Fa-f]+)");
                var code = match.Success ? $"0x{match.Groups[1].Value}" : "unknown";
                return (true, (string?)code);
            }

            return (false, (string?)null);
        }, timeout);
    }

    // ═══════════════════════════════════════════════════════════════
    //  EVENT PUMP (called by DbgEngThread when idle + target running)
    // ═══════════════════════════════════════════════════════════════

    private void PumpEvents()
    {
        if (_client == null) return;

        // For live kernel targets, WaitForEvent must use INFINITE. This wait is
        // also what actually runs the target after SetExecutionStatus(GO).
        // Do not periodically interrupt it; foreground tool calls wake it via
        // WorkQueuedWhilePumpingAction only when there is queued work to run.
        var hr = _client.Control.TryWaitForEvent(
            DEBUG_WAIT.DEFAULT,
            DbgEngEventHandling.InfiniteWaitMilliseconds);

        var nonBreakingPumpWakePending =
            Interlocked.Exchange(ref _nonBreakingPumpWakePending, 0) != 0;

        var status = hr == HRESULT.S_OK
            ? _client.Control.ExecutionStatus
            : DEBUG_STATUS.NO_CHANGE;

        switch (DbgEngEventHandling.ClassifyPumpResult(
            hr,
            status,
            _eventCallbacks.HasBreakingEvent,
            nonBreakingPumpWakePending,
            Volatile.Read(ref _explicitBreakInterruptPending) != 0))
        {
            case DbgEngPumpOutcome.KeepPumping:
                break;

            case DbgEngPumpOutcome.ResumeInternalYieldBreak:
                // Some DbgEng versions/sessions surface DEBUG_INTERRUPT_EXIT as
                // S_OK + BREAK without an event callback instead of E_PENDING.
                // Since our own EXIT interrupt succeeded and no breaking callback
                // ran, this is an internal pump yield, not a user event.
                var resumeHr = _client.Control.TrySetExecutionStatus(
                    DbgEngEventHandling.GetContinueExecutionStatus());
                if (resumeHr == HRESULT.S_OK)
                {
                    _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.GO);
                }
                else
                {
                    _logger.LogWarning(
                        "Event pump could not resume internal wake break: {HResult}",
                        DbgEngEventHandling.FormatHResult(resumeHr));
                    _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.BREAK);
                    _thread.PumpEnabled = false;
                }
                break;

            case DbgEngPumpOutcome.StopOnBreakingEvent:
                // Real event (breakpoint, exception, system error) — stop pumping.
                _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.BREAK);
                _thread.PumpEnabled = false;
                break;

            case DbgEngPumpOutcome.StopOnUnknownBreak:
                // With EXIT-based wakeups, an unclassified break is not known to be
                // synthetic. Leave the target halted for the user instead of
                // automatically resuming a possibly real event.
                _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.BREAK);
                _logger.LogWarning("Event pump stopped on unclassified debugger break.");
                _thread.PumpEnabled = false;
                break;

            case DbgEngPumpOutcome.StopOnUnexpectedFailure:
                _logger.LogWarning(
                    "Event pump stopped after WaitForEvent returned {HResult}",
                    DbgEngEventHandling.FormatHResult(hr));
                _thread.PumpEnabled = false;
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════

    private DbgEngInterruptTimer CreateInterruptTimer(
        DbgEngInterruptPurpose purpose,
        int dueTimeMs) =>
        new(p => Interrupt(p, waitForCompletion: true), purpose, dueTimeMs, _logger);

    private void ArmPumpWait()
    {
        Volatile.Write(ref _pumpWaitActive, 1);
    }

    private void DisarmPumpWait()
    {
        Volatile.Write(ref _pumpWaitActive, 0);
        Volatile.Write(ref _nonBreakingPumpWakePending, 0);
    }

    private void WakePumpForQueuedWork()
    {
        if (Volatile.Read(ref _pumpWaitActive) == 0)
            return;

        Volatile.Write(ref _nonBreakingPumpWakePending, 1);
        QueueInterrupt(DbgEngInterruptPurpose.EventPumpYield);
    }

    private void QueueInterrupt(DbgEngInterruptPurpose purpose)
    {
        Interrupt(purpose, waitForCompletion: false);
    }

    private bool Interrupt(DbgEngInterruptPurpose purpose, bool waitForCompletion)
    {
        try
        {
            return _interruptor?.Interrupt(purpose, waitForCompletion) == true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring failed DbgEng interrupt for {Purpose}", purpose);
            return false;
        }
    }

    internal static TimeSpan GetConnectOperationTimeout(TimeoutConfig timeouts) =>
        TimeSpan.FromSeconds(
            Math.Max(0, timeouts.KdConnectSeconds) +
            Math.Max(0, timeouts.KdInitialBreakSeconds) +
            5);

    internal static TimeSpan GetBreakOperationTimeout(TimeoutConfig timeouts) =>
        TimeSpan.FromSeconds(Math.Max(0, timeouts.KdBreakSeconds) + 3);

    private void SetPumpEnabledForRunningTarget()
    {
        // DbgEng SetExecutionStatus(GO) only requests execution; live target
        // execution actually occurs while WaitForEvent is active. Keep the
        // pump enabled for running targets, but wake it only when foreground
        // MCP work is queued.
        _thread.PumpEnabled = true;
    }

    private Task<string> FormatCurrentDebugEventAsync()
    {
        return _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                throw new InvalidOperationException("Not connected.");

            _thread.PumpEnabled = false;
            _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.BREAK);

            _outputCapture.Clear();
            _client.Control.TryExecute(
                DEBUG_OUTCTL.THIS_CLIENT, ".lastevent", DEBUG_EXECUTE.DEFAULT);
            var lastEvent = _outputCapture.GetAndClear().Trim();

            var events = _eventCallbacks.DrainEvents();
            var eventSummary = events.Count > 0
                ? "\nQueued events:\n" + string.Join("\n", events.Select(e => $"  {e}"))
                : "";

            return $"Debug event received! Target is now halted.\n{lastEvent}{eventSummary}";
        }, TimeSpan.FromSeconds(5));
    }

    private string FormatTargetHaltedMessage()
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected.");

        _outputCapture.Clear();
        _client.Control.TryExecute(
            DEBUG_OUTCTL.THIS_CLIENT, ".lastevent", DEBUG_EXECUTE.DEFAULT);
        var lastEvent = _outputCapture.GetAndClear().Trim();

        return $"Target halted. {lastEvent}\n" +
               "Use kd_execute to inspect state (e.g., 'k' for stack, 'r' for registers).";
    }

    private static string? FindDebuggerDirectory()
    {
        // Try common locations for Debugging Tools for Windows
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64",
            @"C:\Program Files\Windows Kits\10\Debuggers\x64",
            @"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64",
            @"C:\Debuggers",
        };

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "dbgeng.dll")))
                return dir;
        }

        // Also check if WinDbg Preview is installed
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windbgPreview = Path.Combine(localAppData, "Microsoft", "WindowsApps");
        if (File.Exists(Path.Combine(windbgPreview, "dbgeng.dll")))
            return windbgPreview;

        return null; // Will try system PATH
    }

    /// <summary>
    /// Reset connection state without disposing the manager.
    /// Used by snapshot restore — the client was already cleanly disconnected
    /// before the restore, so we just null the reference and stop pumping.
    /// The manager stays fully usable for the next kd_connect.
    /// </summary>
    public void ResetConnectionState()
    {
        _thread.PumpEnabled = false;
        _interruptor?.Dispose();
        _interruptor = null;
        _client = null;
        _isAttached = false;
        _transport = KdTransport.None;
        Volatile.Write(ref _pumpWaitActive, 0);
        Volatile.Write(ref _nonBreakingPumpWakePending, 0);
        Volatile.Write(ref _explicitBreakInterruptPending, 0);
        _eventCallbacks.ClearEvents();
        _eventCallbacks.SetExecutionStatus(DEBUG_STATUS.NO_DEBUGGEE);
        // _disposed intentionally NOT set — manager remains usable
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _thread.PumpEnabled = false;

        try
        {
            if (_client != null)
            {
                _interruptor?.Dispose();
                _interruptor = null;
                _client.TryEndSession(DEBUG_END.ACTIVE_DETACH);
                _client = null;
                _isAttached = false;
                _transport = KdTransport.None;
                Volatile.Write(ref _pumpWaitActive, 0);
                Volatile.Write(ref _nonBreakingPumpWakePending, 0);
                _eventCallbacks.ClearEvents();
            }
        }
        catch { }
    }
}
