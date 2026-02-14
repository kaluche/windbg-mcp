using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WinDbgMCP.Server.KernelDebug;

/// <summary>
/// Dedicated thread for ALL DbgEng COM operations.
/// DbgEng has strict thread affinity — all calls must happen on the thread
/// that called DebugCreate. This class marshals work items to that thread.
/// </summary>
public sealed class DbgEngThread : IDisposable
{
    private readonly Thread _thread;
    private readonly BlockingCollection<WorkItem> _workQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private volatile bool _disposed;

    /// <summary>
    /// Set by DbgEngManager after initialization to allow the thread to pump events.
    /// </summary>
    public Action? PumpEventsAction { get; set; }

    /// <summary>
    /// Whether event pumping is enabled (only when target is running).
    /// </summary>
    public volatile bool PumpEnabled;

    public DbgEngThread(ILogger logger)
    {
        _logger = logger;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DbgEng-Thread"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Run()
    {
        _logger.LogInformation("DbgEng thread started (ThreadId={ThreadId})", Environment.CurrentManagedThreadId);

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // Priority 1: Process any queued tool calls
                if (_workQueue.TryTake(out var work, TimeSpan.FromMilliseconds(0)))
                {
                    try
                    {
                        work.Execute();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Work item failed on DbgEng thread");
                        work.SetException(ex);
                    }
                    continue;
                }

                // Priority 2: If no tool calls pending and pump enabled, pump events
                if (PumpEnabled && PumpEventsAction != null)
                {
                    try
                    {
                        PumpEventsAction();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Event pump iteration failed");
                    }
                    continue;
                }

                // Nothing to do — brief sleep to avoid busy-waiting
                Thread.Sleep(50);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }

        _logger.LogInformation("DbgEng thread exiting");
    }

    /// <summary>
    /// Execute a function on the DbgEng thread and return the result.
    /// </summary>
    public Task<T> ExecuteAsync<T>(Func<T> work, TimeSpan timeout)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DbgEngThread));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource(timeout);

        var item = new WorkItem(() =>
        {
            if (cts.IsCancellationRequested)
            {
                tcs.TrySetCanceled();
                return;
            }

            try
            {
                var result = work();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        // Register timeout cancellation
        cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

        _workQueue.Add(item);
        return tcs.Task;
    }

    /// <summary>
    /// Execute a void action on the DbgEng thread.
    /// </summary>
    public Task ExecuteAsync(Action work, TimeSpan timeout)
    {
        return ExecuteAsync<object?>(() => { work(); return null; }, timeout);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _workQueue.CompleteAdding();

        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(5));

        _cts.Dispose();
        _workQueue.Dispose();
    }

    private class WorkItem
    {
        private readonly Action _action;
        private Exception? _exception;

        public WorkItem(Action action) => _action = action;

        public void Execute() => _action();

        public void SetException(Exception ex)
        {
            _exception = ex;
        }
    }
}
