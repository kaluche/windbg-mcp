namespace WinDbgMCP.Server.State;

/// <summary>
/// LLM-oriented error messages. Every message follows:
/// [WHAT HAPPENED] — [WHY IT HAPPENED] — [WHAT TO DO NEXT]
/// </summary>
public static class ErrorMessages
{
    // === VM State Errors ===
    public const string VmIsOff =
        "VM is powered off. Call vm_start to boot the VM before performing this operation.";

    public const string VmIsPaused =
        "VM is paused (via vm_pause). Call vm_resume to unpause, then retry.";

    public const string VmAlreadyRunning =
        "VM is already running. No action needed — you can proceed with other operations.";

    public const string VmNotOff =
        "VM is not powered off. Call vm_stop first if you need to start fresh.";

    // === Kernel Debug State Errors ===
    public const string KdNotConnected =
        "Kernel debugger is not connected. Call kd_connect to attach to the target VM's kernel.";

    public const string KdAlreadyConnected =
        "Kernel debugger is already connected. Call kd_disconnect first if you need to reconnect.";

    public const string TargetNotBroken =
        "Cannot inspect target — it is currently running freely. " +
        "Memory reads, register dumps, and stack traces require the target to be halted. " +
        "Call kd_break to halt the target, then retry.";

    public const string TargetAlreadyBroken =
        "Target is already halted at a breakpoint. " +
        "You can inspect state with kd_execute (e.g., 'k', 'r', 'db addr'), " +
        "or resume execution (kd_continue).";

    public const string WaitPending =
        "A previous step or continue operation has a pending WaitForEvent. " +
        "Call kd_wait_for_event to check if it completed, or kd_break to interrupt it.";

    // === Guest Operation Errors ===
    public const string GuestFrozenByKd =
        "VM is frozen — kernel debugger is at a breakpoint. " +
        "The entire guest OS is halted, so commands and file transfers will hang. " +
        "Call kd_continue to resume the target, wait 2-3 seconds for VMware Tools " +
        "to recover, then retry this guest operation.";

    public const string ToolsNotResponding =
        "VMware Tools is not responding inside the guest. Possible causes: " +
        "(1) VM is still booting — wait 10-30 seconds and retry. " +
        "(2) Guest OS crashed — check vm_screenshot. " +
        "(3) VMware Tools not installed — cannot execute guest operations without it. " +
        "Call get_system_state for current status.";

    // === Timeout Errors ===
    public static string OperationTimedOut(string operation, double seconds) =>
        $"{operation} timed out after {seconds}s. The operation may still be in progress. " +
        "Call get_system_state to check current status before retrying.";

    // === Connection Errors ===
    public const string KdConnectFailed =
        "Failed to connect kernel debugger. Verify: " +
        "(1) VM is running with debug boot configuration enabled. " +
        "(2) KDNET port/key or serial pipe name is correct. " +
        "(3) No other debugger is already attached to this target.";

    public const string SnapshotRestoredWarning =
        "Snapshot restored successfully. WARNING: All debug sessions have been invalidated. " +
        "Kernel debugger: disconnected. Frida sessions: terminated. dbgsrv: disconnected. " +
        "You must re-establish any debug sessions you need.";

    // === BSOD-Specific Errors ===
    public static string BsodCannotResume(string? bugcheckCode) =>
        $"BSOD — Bugcheck {bugcheckCode ?? "unknown"}. " +
        "The OS has crashed and cannot be meaningfully resumed. " +
        "Continuing will likely re-enter the bugcheck handler or hang. " +
        "Options: " +
        "(1) kd_execute('!analyze -v') to analyze the crash. " +
        "(2) vm_snapshot_restore to revert to a clean state. " +
        "(3) vm_stop(hard=true) then vm_start to reboot.";

    public static string BsodGuestOpsUnavailable(string? bugcheckCode) =>
        $"BSOD DETECTED — Bugcheck {bugcheckCode ?? "unknown"}. " +
        "The guest OS has crashed. Guest operations will NOT work because " +
        "the OS is dead (not just paused). " +
        "Options: (1) kd_execute('!analyze -v') to analyze the crash, " +
        "(2) vm_snapshot_restore to revert to a clean state, " +
        "(3) vm_stop(hard=true) + vm_start to reboot.";

    public static string BsodCannotBreak(string? bugcheckCode) =>
        $"BSOD — cannot resume execution, the OS has crashed " +
        $"(Bugcheck {bugcheckCode ?? "unknown"}). " +
        "Use kd_execute('!analyze -v') to investigate, then " +
        "vm_snapshot_restore to recover.";
}
