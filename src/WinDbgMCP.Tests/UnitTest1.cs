namespace WinDbgMCP.Tests;

public class SystemStateTests
{
    [Fact]
    public void DefaultState_HasSafeDefaults()
    {
        var state = new Server.State.SystemState();
        Assert.Equal(Server.State.VmPowerState.Unknown, state.VmPower);
        Assert.Equal(Server.State.VmToolsState.Unknown, state.VmTools);
        Assert.False(state.KdConnected);
        Assert.Equal(Server.State.DebugExecutionStatus.Uninitialized, state.KdExecStatus);
        Assert.False(state.KdWaitPending);
        Assert.False(state.IsBugcheck);
        Assert.Null(state.BugcheckCode);
        Assert.False(state.GuestOpsAvailable);
        Assert.Null(state.FridaState);
        Assert.Null(state.DbgsrvState);
        Assert.Empty(state.UserDebugSessions);
    }

    [Fact]
    public void FridaSessionState_ToString_ShowsAttached()
    {
        var frida = new Server.State.FridaSessionState
        {
            Connected = true,
            AttachedPid = 1234,
            ProcessName = "notepad.exe"
        };
        Assert.Contains("1234", frida.ToString());
        Assert.Contains("notepad.exe", frida.ToString());
    }

    [Fact]
    public void FridaSessionState_ToString_ShowsDisconnected()
    {
        var frida = new Server.State.FridaSessionState { Connected = false };
        Assert.Contains("Disconnected", frida.ToString());
    }

    [Fact]
    public void DbgsrvSessionState_ToString_ShowsConnected()
    {
        var dbgsrv = new Server.State.DbgsrvSessionState
        {
            Connected = true,
            AttachedPid = 5678
        };
        Assert.Contains("5678", dbgsrv.ToString());
    }

    [Fact]
    public void DbgsrvSessionState_ToString_ShowsDisconnected()
    {
        var dbgsrv = new Server.State.DbgsrvSessionState { Connected = false };
        Assert.Contains("Disconnected", dbgsrv.ToString());
    }
}
