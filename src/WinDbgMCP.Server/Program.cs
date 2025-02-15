using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.State;
using WinDbgMCP.Server.Vmware;

var builder = Host.CreateApplicationBuilder(args);

// Load configuration relative to the executable, not CWD
var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
builder.Configuration.AddJsonFile(Path.Combine(exeDir, "appsettings.json"), optional: false, reloadOnChange: true);

// MCP servers use stdio — route all logs to stderr
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Bind configuration
var config = new ServerConfig();
builder.Configuration.Bind(config);

// Register services as singletons (single MCP server process, single VM)
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<VmwareManager>();
builder.Services.AddSingleton<StateCoordinator>(sp =>
{
    var stateConfig = sp.GetRequiredService<ServerConfig>();
    var logger = sp.GetRequiredService<ILogger<StateCoordinator>>();
    var vmware = sp.GetRequiredService<VmwareManager>();

    var coordinator = new StateCoordinator(stateConfig, logger);

    // Wire up state refresh delegates to the VmwareManager
    coordinator.GetVmPowerStateAsync = () => vmware.GetPowerStateAsync();
    coordinator.AreToolsRunningAsync = (timeout) => vmware.AreToolsRunningAsync(timeout);

    // KD delegates will be wired in Phase 2 when DbgEngManager is added

    return coordinator;
});

// Register MCP server with tools from this assembly
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "WinDbgMCP",
            Version = "1.0.0-alpha"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
