using System.ComponentModel;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.State;
using WinDbgMCP.Server.Vmware;

namespace WinDbgMCP.Server.Tools;

[McpServerToolType]
public static class VmTools
{
    [McpServerTool(Name = "vm_start"), Description(
        "Start the VM. The VM must be powered off. " +
        "After starting, wait for VMware Tools to report 'running' before using guest operations.")]
    public static async Task<string> VmStart(
        StateCoordinator state,
        VmwareManager vmware,
        [Description("If true, start VM without a visible window (default: true)")] bool headless = true,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_start");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var result = await vmware.StartAsync(headless, ct);
            if (!result.Success)
                return $"vm_start failed: {result.Message}";

            return result.Message + " Call get_system_state to check when VMware Tools is running.";
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_start", 60);
        }
    }

    [McpServerTool(Name = "vm_stop"), Description(
        "Stop the VM. Use hard=true for immediate power off, false for graceful shutdown.")]
    public static async Task<string> VmStop(
        StateCoordinator state,
        VmwareManager vmware,
        [Description("If true, force power off. If false, attempt graceful shutdown (default: false)")] bool hard = false,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_stop");
        // vm_stop precheck returns Success with a warning if KD is attached — that's OK
        if (precheck != null && !precheck.IsSuccess) return precheck.ErrorMessage!;

        string warning = precheck?.IsSuccess == true ? precheck.Message + " " : "";

        try
        {
            // If KD is connected, note it will be lost
            if (state.State.KdConnected)
                state.SetKdDisconnected();

            var result = await vmware.StopAsync(hard, ct);
            if (!result.Success)
                return $"vm_stop failed: {result.Message}";

            return warning + result.Message;
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_stop", 30);
        }
    }

    [McpServerTool(Name = "vm_pause"), Description(
        "Pause the VM. EVERYTHING freezes: kernel debugger, guest, network. " +
        "This is different from kd_break! Use vm_resume to unpause.")]
    public static async Task<string> VmPause(
        StateCoordinator state,
        VmwareManager vmware,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_pause");
        if (precheck != null && !precheck.IsSuccess) return precheck.ErrorMessage!;

        string warning = precheck?.IsSuccess == true ? precheck.Message + " " : "";

        try
        {
            var result = await vmware.PauseAsync(ct);
            if (!result.Success)
                return $"vm_pause failed: {result.Message}";

            return warning + result.Message;
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_pause", 10);
        }
    }

    [McpServerTool(Name = "vm_resume"), Description(
        "Resume a paused VM. The VM must be in the Paused state (via vm_pause).")]
    public static async Task<string> VmResume(
        StateCoordinator state,
        VmwareManager vmware,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_resume");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var result = await vmware.UnpauseAsync(ct);
            if (!result.Success)
                return $"vm_resume failed: {result.Message}";

            return result.Message;
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_resume", 10);
        }
    }

    [McpServerTool(Name = "vm_snapshot_create"), Description(
        "Create a named snapshot of the VM. Works in any VM state.")]
    public static async Task<string> VmSnapshotCreate(
        StateCoordinator state,
        VmwareManager vmware,
        [Description("Name for the snapshot")] string name,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_snapshot_create");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var result = await vmware.SnapshotCreateAsync(name, ct);
            if (!result.Success)
                return $"vm_snapshot_create failed: {result.Message}";

            return result.Message;
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_snapshot_create", 120);
        }
    }

    [McpServerTool(Name = "vm_snapshot_restore"), Description(
        "Restore a named snapshot. WARNING: This DESTROYS ALL debug sessions " +
        "(kernel debugger, Frida, dbgsrv). You must reconnect after restoring.")]
    public static async Task<string> VmSnapshotRestore(
        StateCoordinator state,
        VmwareManager vmware,
        [Description("Name of the snapshot to restore")] string name,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_snapshot_restore");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var result = await vmware.SnapshotRestoreAsync(name, ct);
            if (!result.Success)
                return $"vm_snapshot_restore failed: {result.Message}";

            // Reset ALL state
            state.ResetAllState();

            return $"Snapshot '{name}' restored. " + ErrorMessages.SnapshotRestoredWarning;
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_snapshot_restore", 60);
        }
    }

    [McpServerTool(Name = "vm_snapshot_list"), Description(
        "List all snapshots for the VM.")]
    public static async Task<string> VmSnapshotList(
        StateCoordinator state,
        VmwareManager vmware,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_snapshot_list");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var result = await vmware.SnapshotListAsync(ct);
            if (!result.Success)
                return $"vm_snapshot_list failed: {result.ErrorMessage}";

            if (result.Snapshots.Count == 0)
                return "No snapshots found for this VM.";

            return $"Snapshots ({result.Snapshots.Count}):\n" +
                   string.Join("\n", result.Snapshots.Select(s => $"  - {s}"));
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_snapshot_list", 10);
        }
    }

    [McpServerTool(Name = "vm_screenshot"), Description(
        "Capture a screenshot of the VM display. Useful for checking guest OS state " +
        "(boot screen, BSOD, login screen, etc).")]
    public static async Task<string> VmScreenshot(
        StateCoordinator state,
        VmwareManager vmware,
        [Description("Host path to save the screenshot PNG")] string outputPath = @"C:\MCP_Logs\screenshot.png",
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_screenshot");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var result = await vmware.CaptureScreenAsync(outputPath, ct);
            if (!result.Success)
                return $"vm_screenshot failed: {result.Message}";

            return result.Message;
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_screenshot", 10);
        }
    }
}
