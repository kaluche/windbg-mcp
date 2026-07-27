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
    private readonly object _lifecycleLock = new();
    private readonly BlockingCollection<WorkItem> _workQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private Thread _thread;
    private int _activeWorkerGeneration;
    private int _pumpingWorkerGeneration = -1;
    private volatile bool _disposed;

    /// <summary>
    /// Set by DbgEngManager after initialization to allow the thread to pump events.
    /// </summary>
    public Action? PumpEventsAction { get; set; }

    /// <summary>
    /// Called immediately before the worker's final queue check and event-pump
    /// wait. This lets the manager know that any newly queued work must wake
    /// the pump.
    /// </summary>
    public Action? PumpArmingAction { get; set; }

    /// <summary>
    /// Called when the worker leaves the armed/pumping region.
    /// </summary>
    public Action? PumpDisarmedAction { get; set; }

    /// <summary>
    /// Called after a work item is queued while the pump is enabled, allowing
    /// the manager to wake a blocking WaitForEvent without waiting for the
    /// pump's periodic yield timer.
    /// </summary>
    public Action? WorkQueuedWhilePumpingAction { get; set; }

    /// <summary>
    /// Called after the worker is abandoned because a native call or pump wait
    /// exceeded its timeout. The manager uses this to drop stale DbgEng state.
    /// </summary>
    public Action? WorkerWedgedAction { get; set; }

    /// <summary>
    /// Whether event pumping is enabled (only when target is running).
    /// </summary>
    public volatile bool PumpEnabled;

    public DbgEngThread(ILogger logger)
    {
        _logger = logger;
        _thread = StartWorker(0);
    }

    private Thread StartWorker(int generation)
    {
        var thread = new Thread(() => Run(generation))
        {
            IsBackground = true,
            Name = $"DbgEng-Thread-{generation}"
        };
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return thread;
    }

    private void Run(int generation)
    {
        _logger.LogInformation(
            "DbgEng thread started (ThreadId={ThreadId}, Generation={Generation})",
            Environment.CurrentManagedThreadId,
            generation);

        try
        {
            while (!_cts.IsCancellationRequested && IsActiveGeneration(generation))
            {
                // Priority 1: Process any queued tool calls
                if (_workQueue.TryTake(out var work, TimeSpan.FromMilliseconds(0)))
                {
                    ExecuteWorkItem(work, generation);
                    continue;
                }

                // Priority 2: If no tool calls pending and pump enabled, pump events
                if (PumpEnabled && PumpEventsAction != null)
                {
                    try
                    {
                        PumpArmingAction?.Invoke();
                        Volatile.Write(ref _pumpingWorkerGeneration, generation);
                        if (_workQueue.TryTake(out work, TimeSpan.FromMilliseconds(0)))
                        {
                            Volatile.Write(ref _pumpingWorkerGeneration, -1);
                            PumpDisarmedAction?.Invoke();
                            ExecuteWorkItem(work, generation);
                            continue;
                        }

                        PumpEventsAction();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Event pump iteration failed");
                    }
                    finally
                    {
                        Volatile.Write(ref _pumpingWorkerGeneration, -1);
                        PumpDisarmedAction?.Invoke();
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

        _logger.LogInformation(
            "DbgEng thread exiting (ThreadId={ThreadId}, Generation={Generation})",
            Environment.CurrentManagedThreadId,
            generation);
    }

    private bool IsActiveGeneration(int generation) =>
        Volatile.Read(ref _activeWorkerGeneration) == generation;

    private void ExecuteWorkItem(WorkItem work, int generation)
    {
        if (work.IsCancellationRequested)
        {
            work.SetCanceled();
            work.Complete();
            return;
        }

        work.MarkStarted(generation);

        try
        {
            work.Execute();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Work item failed on DbgEng thread");
            work.SetException(ex);
        }
        finally
        {
            work.Complete();
        }
    }

    private void HandleWorkItemTimeout(WorkItem item)
    {
        var restartReason = item.IsRunning
            ? "running work item timed out"
            : IsItemQueuedBehindActivePump(item)
                ? "queued work item timed out behind the event pump"
                : null;

        if (restartReason == null)
            return;

        AbandonCurrentWorker(item.EffectiveGeneration, restartReason);
    }

    private bool IsItemQueuedBehindActivePump(WorkItem item)
    {
        var generation = Volatile.Read(ref _activeWorkerGeneration);
        return item.QueuedGeneration == generation &&
               Volatile.Read(ref _pumpingWorkerGeneration) == generation;
    }

    private void AbandonCurrentWorker(int generation, string reason)
    {
        Action? workerWedgedAction;
        int nextGeneration;

        lock (_lifecycleLock)
        {
            if (_disposed || generation != _activeWorkerGeneration)
                return;

            nextGeneration = generation + 1;
            PumpEnabled = false;
            Volatile.Write(ref _pumpingWorkerGeneration, -1);
            Volatile.Write(ref _activeWorkerGeneration, nextGeneration);
            workerWedgedAction = WorkerWedgedAction;

            _logger.LogError(
                "DbgEng worker generation {Generation} appears wedged ({Reason}); abandoning it and starting generation {NextGeneration}.",
                generation,
                reason,
                nextGeneration);
        }

        try
        {
            workerWedgedAction?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DbgEng worker wedge cleanup action failed.");
        }

        lock (_lifecycleLock)
        {
            if (_disposed || _activeWorkerGeneration != nextGeneration)
                return;

            _thread = StartWorker(nextGeneration);
        }
    }

    /// <summary>
    /// Execute a function on the DbgEng thread and return the result.
    /// </summary>
    public Task<T> ExecuteAsync<T>(Func<T> work, TimeSpan timeout)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DbgEngThread));

        var queuedGeneration = Volatile.Read(ref _activeWorkerGeneration);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var item = new WorkItem(
            () =>
            {
                var result = work();
                tcs.TrySetResult(result);
            },
            ex => tcs.TrySetException(ex),
            () => tcs.TrySetCanceled(),
            timeout,
            queuedGeneration,
            HandleWorkItemTimeout);

        var queued = false;
        try
        {
            _workQueue.Add(item);
            queued = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            item.SetCanceled();
            item.Complete();
        }

        if (queued && PumpEnabled)
        {
            try
            {
                WorkQueuedWhilePumpingAction?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Work-queued pump wake failed");
            }
        }

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
        private readonly Action<Exception> _setException;
        private readonly Action _setCanceled;
        private readonly CancellationTokenSource _timeoutCts;
        private readonly CancellationTokenRegistration _timeoutRegistration;
        private readonly Action<WorkItem> _onTimeout;
        private int _started;
        private int _completed;
        private int _timedOut;
        private int _startedGeneration = -1;

        public WorkItem(
            Action action,
            Action<Exception> setException,
            Action setCanceled,
            TimeSpan timeout,
            int queuedGeneration,
            Action<WorkItem> onTimeout)
        {
            _action = action;
            _setException = setException;
            _setCanceled = setCanceled;
            _onTimeout = onTimeout;
            QueuedGeneration = queuedGeneration;
            _timeoutCts = new CancellationTokenSource(timeout);
            _timeoutRegistration = _timeoutCts.Token.Register(
                static state => ((WorkItem)state!).Timeout(),
                this,
                useSynchronizationContext: false);
        }

        public int QueuedGeneration { get; }
        public int EffectiveGeneration =>
            Volatile.Read(ref _started) != 0 ? Volatile.Read(ref _startedGeneration) : QueuedGeneration;
        public bool IsCancellationRequested => _timeoutCts.IsCancellationRequested;
        public bool IsRunning =>
            Volatile.Read(ref _started) != 0 && Volatile.Read(ref _completed) == 0;

        public void Execute() => _action();

        public void MarkStarted(int generation)
        {
            Volatile.Write(ref _startedGeneration, generation);
            Volatile.Write(ref _started, 1);
        }

        public void SetException(Exception ex) => _setException(ex);

        public void SetCanceled() => _setCanceled();

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            _timeoutRegistration.Dispose();
            _timeoutCts.Dispose();
        }

        private void Timeout()
        {
            if (Interlocked.Exchange(ref _timedOut, 1) != 0)
                return;

            SetCanceled();
            _onTimeout(this);
        }
    }
}
