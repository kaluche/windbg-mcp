using ClrDebug.DbgEng;
using Microsoft.Extensions.Logging;

namespace WinDbgMCP.Server.KernelDebug;

internal sealed class DbgEngInterruptTimer : IDisposable
{
    private const int DisposeCallbackWaitMs = 250;

    private readonly Func<DbgEngInterruptPurpose, bool> _interrupt;
    private readonly DbgEngInterruptPurpose _purpose;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private int _disposed;
    private int _interruptSucceeded;

    public bool InterruptSucceeded => Volatile.Read(ref _interruptSucceeded) != 0;

    public DbgEngInterruptTimer(
        Func<DbgEngInterruptPurpose, bool> interrupt,
        DbgEngInterruptPurpose purpose,
        int dueTimeMs,
        ILogger logger)
    {
        if (dueTimeMs < 0)
            throw new ArgumentOutOfRangeException(nameof(dueTimeMs));

        _interrupt = interrupt;
        _purpose = purpose;
        _logger = logger;
        _timer = new Timer(Interrupt, null, dueTimeMs, Timeout.Infinite);
    }

    private void Interrupt(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            if (_interrupt(_purpose))
                Volatile.Write(ref _interruptSucceeded, 1);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Ignoring failed DbgEng interrupt for {Purpose}",
                _purpose);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        using var callbacksComplete = new ManualResetEvent(false);
        if (_timer.Dispose(callbacksComplete))
        {
            if (!callbacksComplete.WaitOne(DisposeCallbackWaitMs))
            {
                _logger.LogDebug(
                    "DbgEng interrupt timer callback for {Purpose} did not complete within {TimeoutMs}ms; continuing dispose.",
                    _purpose,
                    DisposeCallbackWaitMs);
            }
        }
    }
}
