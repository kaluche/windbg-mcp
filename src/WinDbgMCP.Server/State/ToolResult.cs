namespace WinDbgMCP.Server.State;

/// <summary>
/// Result type returned by tool operations and precondition checks.
/// </summary>
public sealed class ToolResult
{
    public bool IsSuccess { get; }
    public string Message { get; }
    public string? ErrorMessage => IsSuccess ? null : Message;

    private ToolResult(bool success, string message)
    {
        IsSuccess = success;
        Message = message;
    }

    public static ToolResult Success(string message) => new(true, message);
    public static ToolResult Error(string message) => new(false, message);
}
