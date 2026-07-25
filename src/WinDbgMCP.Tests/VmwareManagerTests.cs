using Microsoft.Extensions.Logging.Abstractions;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.Vmware;

namespace WinDbgMCP.Tests;

/// <summary>
/// VmwareManager is constructed (transitively, via StateCoordinator) for EVERY tool
/// invocation. If its constructor throws, the MCP SDK masks it as a generic
/// "An error occurred invoking '<tool>'" and ALL tools — including kernel-debug and
/// Frida tools that don't need VMware — break. These tests pin the construction
/// contract so that regression can't recur silently.
/// </summary>
public class VmwareManagerTests
{
    private static ServerConfig ConfigWith(bool vmwareEnabled, string vmrunPath) => new()
    {
        Vm = new VmConfig { VmwareEnabled = vmwareEnabled, VmrunPath = vmrunPath }
    };

    [Fact]
    public void Construct_VmwareDisabled_DoesNotThrow_WhenVmrunMissing()
    {
        // The whole point of VmwareEnabled=false: run without VMware installed.
        var config = ConfigWith(vmwareEnabled: false, vmrunPath: @"C:\does\not\exist\vmrun.exe");
        var ex = Record.Exception(() =>
            new VmwareManager(config, NullLogger<VmwareManager>.Instance));
        Assert.Null(ex);
    }

    [Fact]
    public void Construct_VmwareEnabled_Throws_WhenVmrunMissing()
    {
        var config = ConfigWith(vmwareEnabled: true, vmrunPath: @"C:\does\not\exist\vmrun.exe");
        Assert.Throws<FileNotFoundException>(() =>
            new VmwareManager(config, NullLogger<VmwareManager>.Instance));
    }
}
