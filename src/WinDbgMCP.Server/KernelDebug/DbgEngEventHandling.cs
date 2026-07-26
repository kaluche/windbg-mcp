using ClrDebug;
using ClrDebug.DbgEng;

namespace WinDbgMCP.Server.KernelDebug;

internal enum DbgEngInterruptPurpose
{
    ExplicitTargetBreak,
    ConnectInitialBreakTimeout,
    BreakWaitTimeout,
    StepTimeout,
    WaitForEventTimeout,
    EventPumpYield
}

internal enum DbgEngWaitOutcome
{
    Event,
    Timeout,
    ExitInterrupt,
    UnexpectedFailure
}

internal enum DbgEngPumpOutcome
{
    KeepPumping,
    StopOnBreakingEvent,
    StopOnUnknownBreak,
    StopOnUnexpectedFailure
}

internal static class DbgEngEventHandling
{
    public const int InfiniteWaitMilliseconds = unchecked((int)0xFFFFFFFF);

    // ClrDebug 0.3.4 does not define HRESULT.E_PENDING, but DbgEng documents
    // it as the normal WaitForEvent result after DEBUG_INTERRUPT_EXIT wakes a
    // wait while the target is still running.
    public const HRESULT E_PENDING = (HRESULT)0x8000000A;

    public static DEBUG_INTERRUPT GetInterrupt(DbgEngInterruptPurpose purpose) =>
        purpose == DbgEngInterruptPurpose.ExplicitTargetBreak
            ? DEBUG_INTERRUPT.ACTIVE
            : DEBUG_INTERRUPT.EXIT;

    public static DbgEngWaitOutcome ClassifyWaitResult(HRESULT hr)
    {
        if (hr == HRESULT.S_OK)
            return DbgEngWaitOutcome.Event;

        if (hr == HRESULT.S_FALSE)
            return DbgEngWaitOutcome.Timeout;

        if (hr == E_PENDING)
            return DbgEngWaitOutcome.ExitInterrupt;

        return DbgEngWaitOutcome.UnexpectedFailure;
    }

    public static bool IsNormalNonEventWaitResult(HRESULT hr)
    {
        var outcome = ClassifyWaitResult(hr);
        return outcome is DbgEngWaitOutcome.Timeout or DbgEngWaitOutcome.ExitInterrupt;
    }

    public static DbgEngPumpOutcome ClassifyPumpResult(
        HRESULT waitHr,
        DEBUG_STATUS executionStatus,
        bool hasBreakingEvent)
    {
        var waitOutcome = ClassifyWaitResult(waitHr);
        if (waitOutcome is DbgEngWaitOutcome.Timeout or DbgEngWaitOutcome.ExitInterrupt)
            return DbgEngPumpOutcome.KeepPumping;

        if (waitOutcome == DbgEngWaitOutcome.UnexpectedFailure)
            return DbgEngPumpOutcome.StopOnUnexpectedFailure;

        if (executionStatus != DEBUG_STATUS.BREAK)
            return DbgEngPumpOutcome.KeepPumping;

        return hasBreakingEvent
            ? DbgEngPumpOutcome.StopOnBreakingEvent
            : DbgEngPumpOutcome.StopOnUnknownBreak;
    }

    public static string FormatHResult(HRESULT hr) =>
        hr == E_PENDING
            ? "0x8000000A (E_PENDING)"
            : $"0x{(uint)hr:X8} ({hr})";
}
