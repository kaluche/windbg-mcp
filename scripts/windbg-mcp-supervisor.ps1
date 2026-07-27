param(
    [string]$ServerExe = "C:\DATA\tools\windbg-mcp\src\WinDbgMCP.Server\bin\Debug\net8.0-windows\win-x64\WinDbgMCP.Server.exe",
    [string]$BindHost = "0.0.0.0",
    [int]$Port = 8002,
    [string]$LogDir = "C:\DATA\tools\windbg-mcp\logs",
    [int]$RestartDelaySeconds = 2,
    [int]$HealthCheckSeconds = 2,
    [string]$KdnetKey = "",
    [switch]$NoExitOnDbgEngWedge
)

$ErrorActionPreference = "Continue"

function Resolve-McpProxyCmd {
    $npmProxyCmd = Join-Path $env:APPDATA "npm\mcp-proxy.cmd"
    if (Test-Path -LiteralPath $npmProxyCmd) {
        return $npmProxyCmd
    }

    $proxyCommand = Get-Command "mcp-proxy.cmd" -ErrorAction SilentlyContinue
    if ($proxyCommand -ne $null) {
        return $proxyCommand.Source
    }

    throw "mcp-proxy.cmd not found. Install it with: npm install -g mcp-proxy"
}

function Get-DescendantProcesses {
    param([Parameter(Mandatory = $true)][int]$RootProcessId)

    $allProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    $pending = @([int]$RootProcessId)
    $seen = @{}
    $descendants = @()

    while ($pending.Count -gt 0) {
        $parentId = [int]$pending[0]
        if ($pending.Count -eq 1) {
            $pending = @()
        } else {
            $pending = @($pending[1..($pending.Count - 1)])
        }

        foreach ($child in $allProcesses) {
            if ($null -eq $child.ParentProcessId -or [int]$child.ParentProcessId -ne $parentId) {
                continue
            }

            $childId = [int]$child.ProcessId
            $childKey = $childId.ToString()
            if ($seen.ContainsKey($childKey)) {
                continue
            }

            $seen[$childKey] = $true
            $descendants += $child
            $pending += $childId
        }
    }

    return @($descendants)
}

function Get-ServerProcesses {
    param([Parameter(Mandatory = $true)][int]$RootProcessId)

    $serverName = Split-Path -Leaf $ServerExe
    @(Get-DescendantProcesses -RootProcessId $RootProcessId |
        Where-Object {
            [string]::Equals($_.Name, $serverName, [StringComparison]::OrdinalIgnoreCase)
        })
}

function Test-LogContains {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Patterns
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    try {
        $content = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        foreach ($pattern in $Patterns) {
            if ($content.IndexOf($pattern, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $true
            }
        }
    } catch {
    }

    return $false
}

function Test-ServerStartedFromLogs {
    param(
        [Parameter(Mandatory = $true)][string]$StdoutLog,
        [Parameter(Mandatory = $true)][string]$StderrLog
    )

    (Test-LogContains $StdoutLog @("starting server on port")) -or
    (Test-LogContains $StderrLog @("Application started", "transport reading messages"))
}

function Test-ServerStoppedFromLogs {
    param(
        [Parameter(Mandatory = $true)][string]$StdoutLog,
        [Parameter(Mandatory = $true)][string]$StderrLog
    )

    (Test-LogContains $StdoutLog @("received shutdown signal", "shutting down")) -or
    (Test-LogContains $StderrLog @("Application is shutting down", "message processing canceled", "transport message reading canceled"))
}

function Stop-ProcessTree {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue)
    foreach ($child in $children) {
        Stop-ProcessTree -ProcessId $child.ProcessId
    }

    try {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    } catch {
    }
}

function Write-Log {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)][string]$Log
    )

    $line = "[$(Get-Date -Format o)] $Message"
    Write-Host $line
    $line | Out-File -FilePath $Log -Append -Encoding utf8
}

# Normal WinDbgMCP configuration is owned by WinDbgMCP.Server.exe:
# appsettings.json beside the EXE, inherited WINDBG_MCP_* environment variables,
# and command-line arguments. The supervisor only forces the wedge behavior that
# makes supervision useful.
if (-not $NoExitOnDbgEngWedge) {
    [Environment]::SetEnvironmentVariable("WINDBG_MCP_KernelDebug__ExitProcessOnDbgEngWedge", "true", "Process")
}
if (-not [string]::IsNullOrWhiteSpace($KdnetKey)) {
    [Environment]::SetEnvironmentVariable("WINDBG_MCP_KDNET_KEY", $KdnetKey, "Process")
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$nodeDir = "C:\Program Files\nodejs"
if (Test-Path -LiteralPath (Join-Path $nodeDir "node.exe")) {
    $pathParts = @($env:PATH -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if (-not ($pathParts | Where-Object { [string]::Equals($_, $nodeDir, [StringComparison]::OrdinalIgnoreCase) })) {
        $env:PATH = "$nodeDir;$env:PATH"
    }
}

$proxyCmd = Resolve-McpProxyCmd

while ($true) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $log = Join-Path $LogDir "windbg-mcp-$stamp.log"
    $stdoutLog = Join-Path $LogDir "windbg-mcp-$stamp.stdout.log"
    $stderrLog = Join-Path $LogDir "windbg-mcp-$stamp.stderr.log"

    if (-not (Test-Path -LiteralPath $ServerExe)) {
        Write-Log "server exe not found: $ServerExe" $log
        Start-Sleep -Seconds 10
        continue
    }

    Write-Log "starting windbg-mcp on ${BindHost}:${Port}" $log

    $serverDir = Split-Path -Parent $ServerExe
    $appsettings = Join-Path $serverDir "appsettings.json"
    $appsettingsStatus = if (Test-Path -LiteralPath $appsettings) { "found" } else { "not found" }
    Write-Log "server config source: appsettings.json $appsettingsStatus at $appsettings; inherited WINDBG_MCP_* env vars still apply" $log
    Write-Log "stdout log: $stdoutLog" $log
    Write-Log "stderr log: $stderrLog" $log

    $proxyCommandLine = '""{0}" --host {1} --port {2} -- "{3}""' -f $proxyCmd, $BindHost, $Port, $ServerExe

    $proxyProcess = Start-Process `
        -FilePath $env:ComSpec `
        -ArgumentList @("/d", "/s", "/c", $proxyCommandLine) `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog `
        -PassThru `
        -NoNewWindow

    Write-Log "started mcp-proxy wrapper pid=$($proxyProcess.Id) via $proxyCmd" $log

    $serverProcessSeen = $false
    $serverLogSeen = $false
    $startupDeadline = (Get-Date).AddSeconds(20)
    $restartReason = $null

    while (-not $proxyProcess.HasExited) {
        Start-Sleep -Seconds $HealthCheckSeconds

        $serverProcesses = Get-ServerProcesses -RootProcessId $proxyProcess.Id
        if ($serverProcesses.Count -gt 0) {
            if (-not $serverProcessSeen) {
                $serverProcessSeen = $true
                $pids = ($serverProcesses | ForEach-Object { $_.ProcessId }) -join ","
                Write-Log "server process detected pid=$pids" $log
            }
            continue
        }

        if (-not $serverLogSeen -and (Test-ServerStartedFromLogs $stdoutLog $stderrLog)) {
            $serverLogSeen = $true
            Write-Log "server startup detected from child logs" $log
            continue
        }

        if (Test-ServerStoppedFromLogs $stdoutLog $stderrLog) {
            $restartReason = "server shutdown detected from child logs"
            break
        }

        if ($serverProcessSeen) {
            $restartReason = "server process exited while mcp-proxy was still running"
            break
        }

        if (-not $serverLogSeen -and (Get-Date) -gt $startupDeadline) {
            $restartReason = "server process did not appear within startup deadline"
            break
        }
    }

    if ($restartReason -ne $null) {
        Write-Log "$restartReason; killing mcp-proxy pid=$($proxyProcess.Id)" $log
        Stop-ProcessTree -ProcessId $proxyProcess.Id
    } else {
        $proxyProcess.Refresh()
        Write-Log "mcp-proxy exited with code $($proxyProcess.ExitCode)" $log
    }

    Write-Log "restarting in ${RestartDelaySeconds}s" $log

    Start-Sleep -Seconds $RestartDelaySeconds
}
