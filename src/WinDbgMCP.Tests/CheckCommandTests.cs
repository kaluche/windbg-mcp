using WinDbgMCP.Server.KernelDebug.Interop;

namespace WinDbgMCP.Tests;

public class CheckCommandTests
{
    [Theory]
    [InlineData("g")]
    [InlineData("gc")]
    [InlineData("gh")]
    [InlineData("gn")]
    [InlineData("gu")]
    [InlineData("gN")]
    [InlineData("t")]
    [InlineData("p")]
    [InlineData("ta")]
    [InlineData("pa")]
    [InlineData("tc")]
    [InlineData("pc")]
    [InlineData("tt")]
    [InlineData("pt")]
    [InlineData("th")]
    [InlineData("ph")]
    [InlineData("wt")]
    [InlineData("q")]
    [InlineData("qq")]
    [InlineData(".detach")]
    [InlineData(".restart")]
    [InlineData(".reboot")]
    public void BlockedCommands_AreDetected(string cmd)
    {
        var (isBlocked, blockedCmd, suggestion) = DbgEngConstants.CheckCommand(cmd);
        Assert.True(isBlocked, $"'{cmd}' should be blocked");
        Assert.Equal(cmd, blockedCmd);
        Assert.NotEmpty(suggestion);
    }

    [Theory]
    [InlineData("k")]
    [InlineData("r")]
    [InlineData("lm")]
    [InlineData("db 0x12345")]
    [InlineData("!process 0 0")]
    [InlineData("!analyze -v")]
    [InlineData("bp nt!NtCreateFile")]
    [InlineData("u rip")]
    [InlineData(".reload /f")]
    [InlineData("x nt!NtCreate*")]
    [InlineData("dv")]
    [InlineData("dd rsp L10")]
    public void SafeCommands_AreAllowed(string cmd)
    {
        var (isBlocked, _, _) = DbgEngConstants.CheckCommand(cmd);
        Assert.False(isBlocked, $"'{cmd}' should be allowed");
    }

    [Fact]
    public void CompoundCommand_BlocksIfAnyPartBlocked()
    {
        var (isBlocked, blockedCmd, _) = DbgEngConstants.CheckCommand("bp nt!NtCreateFile; g");
        Assert.True(isBlocked);
        Assert.Equal("g", blockedCmd);
    }

    [Fact]
    public void CompoundCommand_AllowsIfAllSafe()
    {
        var (isBlocked, _, _) = DbgEngConstants.CheckCommand("bp nt!NtCreateFile; k; r");
        Assert.False(isBlocked);
    }

    [Fact]
    public void BlockedCommand_WithArgs_StillBlocked()
    {
        var (isBlocked, blockedCmd, _) = DbgEngConstants.CheckCommand("g @$ra");
        Assert.True(isBlocked);
        Assert.Equal("g", blockedCmd);
    }

    [Theory]
    [InlineData("G")]
    [InlineData("T")]
    [InlineData("P")]
    [InlineData("Q")]
    public void BlockedCommands_CaseInsensitive(string cmd)
    {
        var (isBlocked, _, _) = DbgEngConstants.CheckCommand(cmd);
        Assert.True(isBlocked, $"'{cmd}' should be blocked (case-insensitive)");
    }

    [Fact]
    public void GoCommands_SuggestKdContinue()
    {
        var (_, _, suggestion) = DbgEngConstants.CheckCommand("g");
        Assert.Contains("kd_continue", suggestion);
    }

    [Fact]
    public void StepIntoCommands_SuggestKdStep()
    {
        var (_, _, suggestion) = DbgEngConstants.CheckCommand("t");
        Assert.Contains("kd_step", suggestion);
        Assert.Contains("into", suggestion);
    }

    [Fact]
    public void StepOverCommands_SuggestKdStep()
    {
        var (_, _, suggestion) = DbgEngConstants.CheckCommand("p");
        Assert.Contains("kd_step", suggestion);
        Assert.Contains("over", suggestion);
    }

    [Fact]
    public void QuitCommands_SuggestKdDisconnect()
    {
        var (_, _, suggestion) = DbgEngConstants.CheckCommand("q");
        Assert.Contains("kd_disconnect", suggestion);
    }

    [Fact]
    public void DetachCommand_SuggestKdDisconnect()
    {
        var (_, _, suggestion) = DbgEngConstants.CheckCommand(".detach");
        Assert.Contains("kd_disconnect", suggestion);
    }

    [Fact]
    public void EmptyCommand_IsAllowed()
    {
        var (isBlocked, _, _) = DbgEngConstants.CheckCommand("");
        Assert.False(isBlocked);
    }
}
