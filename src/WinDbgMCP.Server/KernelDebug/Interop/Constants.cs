namespace WinDbgMCP.Server.KernelDebug.Interop;

/// <summary>
/// DbgEng HRESULT values and constants used throughout the kernel debug layer.
/// </summary>
public static class DbgEngConstants
{
    // HRESULT values
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_PENDING = unchecked((int)0x8000000A);

    // DbgEng-specific HRESULT
    public const int HR_TIMEOUT = unchecked((int)0x80070079); // ERROR_SEM_TIMEOUT as HRESULT
    public const int HR_UNEXPECTED = unchecked((int)0x8000FFFF);

    // DEBUG_ANY_ID — used when creating breakpoints to let engine assign ID
    public const uint DEBUG_ANY_ID = 0xFFFFFFFF;

    // WaitForEvent timeout: INFINITE
    public const uint INFINITE = 0xFFFFFFFF;

    /// <summary>
    /// Execution-control commands that are BLOCKED inside kd_execute.
    /// These cause WaitForEvent internally and would deadlock.
    /// </summary>
    public static readonly HashSet<string> BlockedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // Go variants
        "g", "gc", "gh", "gn", "gu", "gN",
        // Step variants
        "t", "p",
        "ta", "pa",
        "tc", "pc",
        "tt", "pt",
        "th", "ph",
        "wt",
        // Session destroyers
        "q", "qq",
        ".detach",
        ".restart",
        ".reboot",
    };

    /// <summary>
    /// Checks if a command would deadlock or corrupt state.
    /// Handles compound commands ("bp foo; g") and commands with args ("g @$ra").
    /// </summary>
    public static (bool IsBlocked, string BlockedCmd, string Suggestion) CheckCommand(string command)
    {
        var parts = command.Split(';', StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var baseCmd = part.Split(' ', 2, StringSplitOptions.TrimEntries)[0];

            if (BlockedCommands.Contains(baseCmd))
            {
                string suggestion = baseCmd switch
                {
                    "g" or "gc" or "gh" or "gn" or "gN"
                        => "Use kd_continue instead (which handles the async wait safely).",
                    "gu"
                        => "Use kd_continue instead. To run until return, set a breakpoint " +
                           "on the return address first: kd_execute('bp @$ra'), then kd_continue.",
                    "t" or "ta" or "tc" or "tt" or "th"
                        => "Use kd_step(mode='into') instead (which has a timeout).",
                    "p" or "pa" or "pc" or "pt" or "ph"
                        => "Use kd_step(mode='over') instead (which has a timeout).",
                    "wt"
                        => "wt can run for minutes. Use kd_step(mode='over') for single steps, " +
                           "or set a breakpoint and kd_continue instead.",
                    "q" or "qq"
                        => "Use kd_disconnect to safely end the debug session.",
                    ".detach"
                        => "Use kd_disconnect to safely detach.",
                    ".restart" or ".reboot"
                        => "Use vm_stop + vm_start to restart, or vm_snapshot_restore to revert.",
                    _ => "Use the appropriate dedicated tool instead."
                };

                return (true, baseCmd, suggestion);
            }
        }

        return (false, "", "");
    }
}
