using ClrDebug.DbgEng;
using Microsoft.Extensions.Logging;

namespace WinDbgMCP.Server.KernelDebug;

internal sealed class DbgEngInterruptTimer : IDisposable
{
    private readonly Func<DebugClient?> _getClient;
    private readonly DEBUG_INTERRUPT _interrupt;
    private readonly DbgEngInterruptPurpose _purpose;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private int _disposed;

    public DbgEngInterruptTimer(
        Func<DebugClient?> getClient,
        DbgEngInterruptPurpose purpose,
        int dueTimeMs,
        ILogger logger)
    {
        if (dueTimeMs < 0)
            throw new ArgumentOutOfRangeException(nameof(dueTimeMs));

        _getClient = getClient;
        _purpose = purpose;
        _logger = logger;
        _interrupt = DbgEngEventHandling.GetInterrupt(purpose);
        _timer = new Timer(Interrupt, null, dueTimeMs, Timeout.Infinite);
    }

    private void Interrupt(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _getClient()?.Control.SetInterrupt(_interrupt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Ignoring failed DbgEng interrupt {Interrupt} for {Purpose}",
                _interrupt,
                _purpose);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        using var callbacksComplete = new ManualResetEvent(false);
        if (_timer.Dispose(callbacksComplete))
            callbacksComplete.WaitOne();
    }
}
