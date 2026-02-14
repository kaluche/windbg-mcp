using WinDbgMCP.Server.State;

namespace WinDbgMCP.Tests;

public class ToolResultTests
{
    [Fact]
    public void Success_SetsIsSuccessTrue()
    {
        var result = ToolResult.Success("Operation completed");
        Assert.True(result.IsSuccess);
        Assert.Equal("Operation completed", result.Message);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Error_SetsIsSuccessFalse()
    {
        var result = ToolResult.Error("Something failed");
        Assert.False(result.IsSuccess);
        Assert.Equal("Something failed", result.Message);
        Assert.Equal("Something failed", result.ErrorMessage);
    }
}
