namespace WinDbgMCP.Server.Guest.Models;

/// <summary>
/// Result of a command executed inside the guest VM.
/// </summary>
public sealed class GuestCommandResult
{
    public bool Success { get; }
    public int ExitCode { get; }
    public string Stdout { get; }
    public string Stderr { get; }
    public string? ErrorMessage { get; }

    private GuestCommandResult(bool success, int exitCode, string stdout, string stderr, string? error = null)
    {
        Success = success;
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
        ErrorMessage = error;
    }

    public static GuestCommandResult Ok(int exitCode, string stdout, string stderr)
        => new(true, exitCode, stdout, stderr);

    public static GuestCommandResult Failed(string error)
        => new(false, -1, "", "", error);

    public override string ToString()
    {
        if (!Success)
            return $"FAILED: {ErrorMessage}";

        var result = $"Exit code: {ExitCode}";
        if (!string.IsNullOrWhiteSpace(Stdout))
            result += $"\n--- stdout ---\n{Stdout}";
        if (!string.IsNullOrWhiteSpace(Stderr))
            result += $"\n--- stderr ---\n{Stderr}";
        return result;
    }
}
