# Windows Configuration

This setup uses a Windows debugger host for WinDbgMCP/DbgEng and a separate Windows target/debuggee configured for KDNET.

## Debugger Host

Install .NET 8 SDK

```powershell
winget install `
  --id Microsoft.DotNet.SDK.8 `
  --source winget `
  --accept-source-agreements `
  --accept-package-agreements
```

Install Windows debugging components : https://learn.microsoft.com/en-us/windows/apps/windows-sdk/

Install Chocolatey : https://chocolatey.org/install

Install Node.js, and `mcp-proxy`

```powershell
choco install nodejs
npm install -g mcp-proxy
```

Confirm nuget source is not empty :

```powershell
dotnet nuget list source
Sources inscrites :
  1.  nuget.org [Activé]
      https://api.nuget.org/v3/index.json
```
If empty : 

```powershell
dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org
```

Build from source:

```powershell
dotnet build src\WinDbgMCP.Server\WinDbgMCP.Server.csproj
```



Or publish a self-contained single-file EXE:

```powershell
dotnet publish src\WinDbgMCP.Server\WinDbgMCP.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish\win-x64
```

For the standalone/no-sidecar mode, configure the EXE through environment variables:

```powershell
$env:WINDBG_MCP_VMWARE_ENABLED="false"
$env:WINDBG_MCP_TARGET_HOST="<TARGET_IP>"
$env:WINDBG_MCP_KDNET_PORT="50000"
$env:WINDBG_MCP_KDNET_KEY="your.kdnet.key.here"
$env:WINDBG_MCP_TRANSCRIPT_DIRECTORY="C:\tmp\windbg-mcp\transcripts"
```

Then run it behind `mcp-proxy`:

```powershell
mcp-proxy --host 0.0.0.0 --port 8002 -- .\publish\win-x64\WinDbgMCP.Server.exe
```

If you prefer file-based configuration, copy `src\WinDbgMCP.Server\appsettings.example.json` to `appsettings.json`, edit it, and place it next to `WinDbgMCP.Server.exe`.

## Debuggee KDNET Configuration

Run these commands in an elevated command prompt on the Windows target/debuggee:

```cmd
bcdedit /debug on
bcdedit /dbgsettings net hostip:<DEBUGGER_HOST_IP> port:50000 key:<YOUR_KEY>
shutdown /r /t 0
```

`hostip` is the debugger host IP, not the target IP. The KDNET key and port must match the values used by WinDbgMCP on the debugger host.

## MCP Client Endpoints

Claude uses SSE:

```bash
claude mcp add --scope project --transport sse windbg-mcp http://<DEBUGGER_HOST>:8002/sse
```

Codex uses streamable HTTP:

```bash
codex mcp add windbg-mcp --url http://<DEBUGGER_HOST>:8002/mcp
```

## Firewall

Allow inbound UDP `50000` on the debugger host for KDNET and inbound TCP `8002` for `mcp-proxy`. If using direct Frida from the operator host, allow TCP `27042` to the target/debuggee.

## Troubleshooting

If `mcp-proxy` reports `MCP error -32000: Connection closed`, run `WinDbgMCP.Server.exe` directly in PowerShell. Common causes are missing Windows debugging components, malformed KDNET configuration, a mismatched KDNET key/port, or a required config value not set through either `appsettings.json` or environment variables.
