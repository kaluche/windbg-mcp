using System.Collections.Concurrent;
using ClrDebug.DbgEng;
using Microsoft.Extensions.Logging;

namespace WinDbgMCP.Server.KernelDebug;

internal sealed class DbgEngInterruptor : IDisposable
{
    private sealed class InterruptRequest
    {
        public InterruptRequest(DEBUG_INTERRUPT interrupt, DbgEngInterruptPurpose purpose)
        {
            Interrupt = interrupt;
            Purpose = purpose;
        }

        public DEBUG_INTERRUPT Interrupt { get; }
        public DbgEngInterruptPurpose Purpose { get; }
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly DebugClient _sourceClient;
    private readonly ILogger _logger;
    private readonly BlockingCollection<InterruptRequest> _requests = new();
    private readonly Thread _thread;
    private int _disposed;

    public DbgEngInterruptor(DebugClient sourceClient, ILogger logger)
    {
        _sourceClient = sourceClient;
        _logger = logger;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DbgEng-Interrupt"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool Interrupt(
        DbgEngInterruptPurpose purpose,
        bool waitForCompletion = false,
        int completionTimeoutMs = 250)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        var request = new InterruptRequest(
            DbgEngEventHandling.GetInterrupt(purpose),
            purpose);

        try
        {
            _requests.Add(request);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!waitForCompletion)
            return true;

        try
        {
            return request.Completion.Task.Wait(completionTimeoutMs) &&
                   request.Completion.Task.Result;
        }
        catch
        {
            return false;
        }
    }

    private void Run()
    {
        DebugClient? interruptClient = null;

        try
        {
            // CreateClient is explicitly documented as the way to create a
            // DbgEng client for the current thread. Use that thread-owned
            // client for all cross-thread SetInterrupt calls.
            interruptClient = _sourceClient.CreateClient();

            foreach (var request in _requests.GetConsumingEnumerable())
            {
                try
                {
                    interruptClient.Control.SetInterrupt(request.Interrupt);
                    request.Completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Ignoring failed DbgEng interrupt {Interrupt} for {Purpose}",
                        request.Interrupt,
                        request.Purpose);
                    request.Completion.TrySetResult(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DbgEng interrupt thread failed.");

            while (_requests.TryTake(out var request))
                request.Completion.TrySetResult(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _requests.CompleteAdding();
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(1));

        _requests.Dispose();
    }
}
