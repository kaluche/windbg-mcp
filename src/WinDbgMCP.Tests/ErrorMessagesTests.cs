using WinDbgMCP.Server.State;

namespace WinDbgMCP.Tests;

public class ErrorMessagesTests
{
    [Fact]
    public void OperationTimedOut_IncludesDetails()
    {
        var msg = ErrorMessages.OperationTimedOut("kd_connect", 30.0);
        Assert.Contains("kd_connect", msg);
        Assert.Contains("30", msg);
        Assert.Contains("get_system_state", msg);
    }

    [Fact]
    public void BsodCannotResume_IncludesBugcheckCode()
    {
        var msg = ErrorMessages.BsodCannotResume("0x0000007E");
        Assert.Contains("0x0000007E", msg);
        Assert.Contains("!analyze -v", msg);
        Assert.Contains("vm_snapshot_restore", msg);
    }

    [Fact]
    public void BsodCannotResume_HandlesNullBugcheck()
    {
        var msg = ErrorMessages.BsodCannotResume(null);
        Assert.Contains("unknown", msg);
    }

    [Fact]
    public void BsodGuestOpsUnavailable_IncludesBugcheckCode()
    {
        var msg = ErrorMessages.BsodGuestOpsUnavailable("0x0000007E");
        Assert.Contains("BSOD", msg);
        Assert.Contains("0x0000007E", msg);
        Assert.Contains("crashed", msg);
    }

    [Fact]
    public void BsodCannotBreak_IncludesBugcheckCode()
    {
        var msg = ErrorMessages.BsodCannotBreak("0x0000007E");
        Assert.Contains("BSOD", msg);
        Assert.Contains("!analyze -v", msg);
    }

    [Fact]
    public void AllConstantErrors_AreNotEmpty()
    {
        Assert.NotEmpty(ErrorMessages.VmIsOff);
        Assert.NotEmpty(ErrorMessages.VmIsPaused);
        Assert.NotEmpty(ErrorMessages.VmAlreadyRunning);
        Assert.NotEmpty(ErrorMessages.VmNotOff);
        Assert.NotEmpty(ErrorMessages.KdNotConnected);
        Assert.NotEmpty(ErrorMessages.KdAlreadyConnected);
        Assert.NotEmpty(ErrorMessages.TargetNotBroken);
        Assert.NotEmpty(ErrorMessages.TargetAlreadyBroken);
        Assert.NotEmpty(ErrorMessages.WaitPending);
        Assert.NotEmpty(ErrorMessages.GuestFrozenByKd);
        Assert.NotEmpty(ErrorMessages.ToolsNotResponding);
        Assert.NotEmpty(ErrorMessages.KdConnectFailed);
        Assert.NotEmpty(ErrorMessages.SnapshotRestoredWarning);
    }

    [Fact]
    public void ErrorMessages_ContainActionableGuidance()
    {
        // Every error message should tell the LLM what to do next
        Assert.Contains("vm_start", ErrorMessages.VmIsOff);
        Assert.Contains("vm_resume", ErrorMessages.VmIsPaused);
        Assert.Contains("kd_connect", ErrorMessages.KdNotConnected);
        Assert.Contains("kd_disconnect", ErrorMessages.KdAlreadyConnected);
        Assert.Contains("kd_break", ErrorMessages.TargetNotBroken);
        Assert.Contains("kd_continue", ErrorMessages.GuestFrozenByKd);
    }
}
