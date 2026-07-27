using Microsoft.Extensions.Logging.Abstractions;
using WinDbgMCP.Server.KernelDebug;

namespace WinDbgMCP.Tests;

public class DbgEngThreadTests
{
    [Fact]
    public async Task TimedOutRunningWorkItem_RestartsWorkerSoLaterWorkCanRun()
    {
        using var releaseBlockedWork = new ManualResetEventSlim(false);
        using var workStarted = new ManualResetEventSlim(false);
        using var thread = new DbgEngThread(NullLogger<DbgEngThread>.Instance);
        var wedgeNotifications = 0;
        thread.WorkerWedgedAction = () => Interlocked.Increment(ref wedgeNotifications);

        var blocked = thread.ExecuteAsync(
            () =>
            {
                workStarted.Set();
                releaseBlockedWork.Wait();
                return 1;
            },
            TimeSpan.FromMilliseconds(100));

        Assert.True(workStarted.Wait(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<TaskCanceledException>(() => blocked);

        try
        {
            var next = await thread.ExecuteAsync(() => 2, TimeSpan.FromSeconds(1));
            Assert.Equal(2, next);
            Assert.Equal(1, Volatile.Read(ref wedgeNotifications));
        }
        finally
        {
            releaseBlockedWork.Set();
        }
    }

    [Fact]
    public async Task QueuedWorkTimedOutBehindActivePump_RestartsWorkerSoLaterWorkCanRun()
    {
        using var releasePump = new ManualResetEventSlim(false);
        using var pumpStarted = new ManualResetEventSlim(false);
        using var thread = new DbgEngThread(NullLogger<DbgEngThread>.Instance);
        var wedgeNotifications = 0;
        thread.WorkerWedgedAction = () => Interlocked.Increment(ref wedgeNotifications);

        thread.PumpEnabled = true;
        thread.PumpEventsAction = () =>
        {
            pumpStarted.Set();
            releasePump.Wait();
        };

        Assert.True(pumpStarted.Wait(TimeSpan.FromSeconds(1)));

        var queued = thread.ExecuteAsync(() => 1, TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TaskCanceledException>(() => queued);

        try
        {
            var next = await thread.ExecuteAsync(() => 2, TimeSpan.FromSeconds(1));
            Assert.Equal(2, next);
            Assert.Equal(1, Volatile.Read(ref wedgeNotifications));
        }
        finally
        {
            releasePump.Set();
        }
    }
}
