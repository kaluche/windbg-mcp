using ClrDebug;
using ClrDebug.DbgEng;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.KernelDebug;
using WinDbgMCP.Server.State;

namespace WinDbgMCP.Tests;

public class DbgEngEventHandlingTests
{
    [Fact]
    public void ExitInterruptHResult_IsNormalNonEventWaitResult()
    {
        var hr = DbgEngEventHandling.E_PENDING;

        Assert.Equal(DbgEngWaitOutcome.ExitInterrupt, DbgEngEventHandling.ClassifyWaitResult(hr));
        Assert.True(DbgEngEventHandling.IsNormalNonEventWaitResult(hr));
    }

    [Fact]
    public void TimeoutHResult_IsNormalNonEventWaitResult()
    {
        var hr = HRESULT.S_FALSE;

        Assert.Equal(DbgEngWaitOutcome.Timeout, DbgEngEventHandling.ClassifyWaitResult(hr));
        Assert.True(DbgEngEventHandling.IsNormalNonEventWaitResult(hr));
    }

    [Fact]
    public void EventPump_KeepsPumpingAfterExitInterruptWake()
    {
        var outcome = DbgEngEventHandling.ClassifyPumpResult(
            DbgEngEventHandling.E_PENDING,
            DEBUG_STATUS.GO,
            hasBreakingEvent: false,
            internalYieldInterruptSucceeded: true,
            explicitBreakInterruptPending: false);

        Assert.Equal(DbgEngPumpOutcome.KeepPumping, outcome);
    }

    [Fact]
    public void EventPump_StopsOnRealBreakingEvent()
    {
        var outcome = DbgEngEventHandling.ClassifyPumpResult(
            HRESULT.S_OK,
            DEBUG_STATUS.BREAK,
            hasBreakingEvent: true,
            internalYieldInterruptSucceeded: true,
            explicitBreakInterruptPending: false);

        Assert.Equal(DbgEngPumpOutcome.StopOnBreakingEvent, outcome);
    }

    [Fact]
    public void EventPump_ResumesUnclassifiedBreakAfterInternalYieldInterrupt()
    {
        var outcome = DbgEngEventHandling.ClassifyPumpResult(
            HRESULT.S_OK,
            DEBUG_STATUS.BREAK,
            hasBreakingEvent: false,
            internalYieldInterruptSucceeded: true,
            explicitBreakInterruptPending: false);

        Assert.Equal(DbgEngPumpOutcome.ResumeInternalYieldBreak, outcome);
    }

    [Fact]
    public void EventPump_StopsOnUnclassifiedBreakWhenExplicitBreakIsPending()
    {
        var outcome = DbgEngEventHandling.ClassifyPumpResult(
            HRESULT.S_OK,
            DEBUG_STATUS.BREAK,
            hasBreakingEvent: false,
            internalYieldInterruptSucceeded: true,
            explicitBreakInterruptPending: true);

        Assert.Equal(DbgEngPumpOutcome.StopOnUnknownBreak, outcome);
    }

    [Fact]
    public void EventPump_StopsOnUnknownBreakWithoutInternalYieldInterrupt()
    {
        var outcome = DbgEngEventHandling.ClassifyPumpResult(
            HRESULT.S_OK,
            DEBUG_STATUS.BREAK,
            hasBreakingEvent: false,
            internalYieldInterruptSucceeded: false,
            explicitBreakInterruptPending: false);

        Assert.Equal(DbgEngPumpOutcome.StopOnUnknownBreak, outcome);
    }

    [Fact]
    public void EventPump_StopsOnUnexpectedHResult()
    {
        var outcome = DbgEngEventHandling.ClassifyPumpResult(
            HRESULT.E_FAIL,
            DEBUG_STATUS.GO,
            hasBreakingEvent: false,
            internalYieldInterruptSucceeded: false,
            explicitBreakInterruptPending: false);

        Assert.Equal(DbgEngPumpOutcome.StopOnUnexpectedFailure, outcome);
    }

    [Fact]
    public void ExplicitBreakPurpose_UsesActiveInterrupt()
    {
        Assert.Equal(
            DEBUG_INTERRUPT.ACTIVE,
            DbgEngEventHandling.GetInterrupt(DbgEngInterruptPurpose.ExplicitTargetBreak));
    }

    [Fact]
    public void ContinueFromBreak_UsesGoHandled()
    {
        Assert.Equal(
            DEBUG_STATUS.GO_HANDLED,
            DbgEngEventHandling.GetContinueExecutionStatus());
    }

    [Theory]
    [InlineData((int)DEBUG_STATUS.GO, (int)DEBUG_STATUS.GO)]
    [InlineData((int)DEBUG_STATUS.GO_HANDLED, (int)DEBUG_STATUS.GO)]
    [InlineData((int)DEBUG_STATUS.GO_NOT_HANDLED, (int)DEBUG_STATUS.GO)]
    [InlineData((int)DEBUG_STATUS.BREAK, (int)DEBUG_STATUS.BREAK)]
    public void InstructionOnlyExecutionStatuses_NormalizeToReportedGo(
        int inputValue,
        int expectedValue)
    {
        var input = (DEBUG_STATUS)inputValue;
        var expected = (DEBUG_STATUS)expectedValue;

        Assert.Equal(expected, DbgEngEventHandling.NormalizeReportedExecutionStatus(input));
    }

    [Fact]
    public void DisconnectPumpWake_UsesActiveInterrupt()
    {
        Assert.Equal(
            DEBUG_INTERRUPT.ACTIVE,
            DbgEngEventHandling.GetInterrupt(DbgEngInterruptPurpose.DisconnectPumpWake));
    }

    [Theory]
    [InlineData((int)DbgEngInterruptPurpose.ConnectInitialBreakTimeout)]
    [InlineData((int)DbgEngInterruptPurpose.BreakWaitTimeout)]
    [InlineData((int)DbgEngInterruptPurpose.StepTimeout)]
    [InlineData((int)DbgEngInterruptPurpose.WaitForEventTimeout)]
    [InlineData((int)DbgEngInterruptPurpose.EventPumpYield)]
    public void WaitCancellationAndYieldPurposes_UseExitInterrupt(int purposeValue)
    {
        var purpose = (DbgEngInterruptPurpose)purposeValue;

        Assert.Equal(DEBUG_INTERRUPT.EXIT, DbgEngEventHandling.GetInterrupt(purpose));
    }

    [Fact]
    public void ConnectOperationTimeout_IncludesAttachAndInitialBreakBudgets()
    {
        var timeouts = new TimeoutConfig
        {
            KdConnectSeconds = 30,
            KdInitialBreakSeconds = 15
        };

        Assert.Equal(TimeSpan.FromSeconds(50), DbgEngManager.GetConnectOperationTimeout(timeouts));
    }

    [Fact]
    public void BreakOperationTimeout_IncludesBreakWaitAndPumpYieldBudget()
    {
        var timeouts = new TimeoutConfig
        {
            KdBreakSeconds = 10
        };

        Assert.Equal(TimeSpan.FromSeconds(13), DbgEngManager.GetBreakOperationTimeout(timeouts));
    }

    [Fact]
    public void FirstChanceException_NonMatchingDefault_IsNotCaptured()
    {
        var callbacks = new DebugEventCallbacks();
        var exception = new EXCEPTION_RECORD64
        {
            ExceptionCode = (NTSTATUS)0xC0000005,
            ExceptionAddress = unchecked((long)0x4141414141414141)
        };

        var status = callbacks.Exception(ref exception, firstChance: 1);

        Assert.Equal(DEBUG_STATUS.GO_NOT_HANDLED, status);
        Assert.False(callbacks.HasBreakingEvent);
        Assert.Equal(0, callbacks.PendingCount);
    }

    [Fact]
    public void FirstChanceBreakpointException_IsCaptured()
    {
        var callbacks = new DebugEventCallbacks();
        var exception = new EXCEPTION_RECORD64
        {
            ExceptionCode = (NTSTATUS)0x80000003,
            ExceptionAddress = unchecked((long)0xFFFFF8017BAFA0D0)
        };

        var status = callbacks.Exception(ref exception, firstChance: 1);

        Assert.Equal(DEBUG_STATUS.BREAK, status);
        Assert.True(callbacks.HasBreakingEvent);
        Assert.Equal(1, callbacks.PendingCount);
    }

    [Fact]
    public void DebugEventCallbacks_ClearEventsDrainsQueueAndBreakingFlag()
    {
        var callbacks = new DebugEventCallbacks();
        var exception = new EXCEPTION_RECORD64
        {
            ExceptionCode = (NTSTATUS)0x80000003,
            ExceptionAddress = unchecked((long)0xFFFFF8017BAFA0D0)
        };
        callbacks.Exception(ref exception, firstChance: 1);

        callbacks.ClearEvents();

        Assert.False(callbacks.HasBreakingEvent);
        Assert.Equal(0, callbacks.PendingCount);
    }

    [Fact]
    public void DebugEventCallbacks_SetExecutionStatusUpdatesCachedStatus()
    {
        var callbacks = new DebugEventCallbacks();

        callbacks.SetExecutionStatus(DEBUG_STATUS.BREAK);

        Assert.Equal(DEBUG_STATUS.BREAK, callbacks.LastExecutionStatus);
    }

    [Fact]
    public void DebugExecutionStatus_ValuesMatchClrDebugDbgEngStatus()
    {
        Assert.Equal((int)DEBUG_STATUS.NO_CHANGE, (int)DebugExecutionStatus.NoChange);
        Assert.Equal((int)DEBUG_STATUS.GO, (int)DebugExecutionStatus.Go);
        Assert.Equal((int)DEBUG_STATUS.GO_HANDLED, (int)DebugExecutionStatus.GoHandled);
        Assert.Equal((int)DEBUG_STATUS.GO_NOT_HANDLED, (int)DebugExecutionStatus.GoNotHandled);
        Assert.Equal((int)DEBUG_STATUS.STEP_OVER, (int)DebugExecutionStatus.StepOver);
        Assert.Equal((int)DEBUG_STATUS.STEP_INTO, (int)DebugExecutionStatus.StepInto);
        Assert.Equal((int)DEBUG_STATUS.BREAK, (int)DebugExecutionStatus.Break);
        Assert.Equal((int)DEBUG_STATUS.NO_DEBUGGEE, (int)DebugExecutionStatus.NoDebuggee);
        Assert.Equal((int)DEBUG_STATUS.STEP_BRANCH, (int)DebugExecutionStatus.StepBranch);
        Assert.Equal((int)DEBUG_STATUS.IGNORE_EVENT, (int)DebugExecutionStatus.IgnoreEvent);
        Assert.Equal((int)DEBUG_STATUS.RESTART_REQUESTED, (int)DebugExecutionStatus.RestartRequested);
    }
}
