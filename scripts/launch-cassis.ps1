# Launch Rhino 8 + Grasshopper and wait until Cassis MCP is listening.
# Cleans stale Rhino / port-3003 holders when MCP is unhealthy so relaunches succeed.
[CmdletBinding()]
param(
    [string]$McpUrl = $(if ($env:CASSIS_MCP_URL) { $env:CASSIS_MCP_URL } else { "http://127.0.0.1:3003/mcp/" }),
    [int]$TimeoutSec = $(if ($env:CASSIS_LAUNCH_TIMEOUT) { [int]$env:CASSIS_LAUNCH_TIMEOUT } else { 90 }),
    [string]$RhinoPath = $(if ($env:CASSIS_RHINO_PATH) { $env:CASSIS_RHINO_PATH } else { "" }),
    [switch]$SkipCleanup = ($env:CASSIS_SKIP_CLEANUP -eq "1")
)

$ErrorActionPreference = "Continue"

# Plain GET to /mcp/ hangs (long-lived SSE). Always POST initialize.
if ($McpUrl -match "localhost") {
    $McpUrl = $McpUrl -replace "localhost", "127.0.0.1"
}

function Test-CassisMcp {
    param([string]$Url)
    try {
        $body = @{
            jsonrpc = "2.0"
            id = 1
            method = "initialize"
            params = @{
                protocolVersion = "2024-11-05"
                capabilities = @{}
                clientInfo = @{ name = "cassis-launch"; version = "1.0" }
            }
        } | ConvertTo-Json -Compress -Depth 6

        # TimeoutSec aborts the hanging SSE stream after the first chunk is usually in.
        $resp = Invoke-WebRequest -Uri $Url -Method Post -Body $body -TimeoutSec 3 -UseBasicParsing `
            -Headers @{
                "Content-Type" = "application/json"
                "Accept" = "application/json, text/event-stream"
                "Connection" = "close"
            }
        $text = [string]$resp.Content
        return ($text -match '"result"' -or $text -match '"serverInfo"')
    } catch {
        # Some hosts throw on SSE timeout but still populated the response.
        $exResp = $_.Exception.Response
        if ($exResp -ne $null) {
            try {
                $stream = $exResp.GetResponseStream()
                $reader = New-Object System.IO.StreamReader($stream)
                $text = $reader.ReadToEnd()
                if ($text -match '"result"' -or $text -match '"serverInfo"') { return $true }
            } catch { }
        }
        return $false
    }
}

function Get-Port3003Pids {
    try {
        $conns = Get-NetTCPConnection -LocalPort 3003 -State Listen -ErrorAction SilentlyContinue
        if ($conns) {
            return @($conns | Select-Object -ExpandProperty OwningProcess -Unique)
        }
    } catch { }
    return @()
}

function Stop-StaleCassis {
    if ($SkipCleanup) {
        Write-Host "Skipping cleanup (CASSIS_SKIP_CLEANUP / -SkipCleanup)"
        return
    }
    if (Test-CassisMcp -Url $McpUrl) { return }

    $rhino = @(Get-Process -Name "Rhino","Rhinoceros" -ErrorAction SilentlyContinue)
    $listeners = Get-Port3003Pids

    if ($rhino.Count -eq 0 -and $listeners.Count -eq 0) { return }

    Write-Host "MCP unhealthy — clearing stale Rhino / port 3003…"

    foreach ($p in $rhino) {
        try {
            $p.CloseMainWindow() | Out-Null
        } catch { }
    }
    Start-Sleep -Seconds 5
    foreach ($p in @(Get-Process -Name "Rhino","Rhinoceros" -ErrorAction SilentlyContinue)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
    }

    foreach ($procId in (Get-Port3003Pids)) {
        Write-Host "Killing leftover listener PID $procId"
        try { Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue } catch { }
    }
    Start-Sleep -Seconds 1
}

function Resolve-RhinoPath {
    param([string]$Hint)
    if ($Hint -and (Test-Path $Hint)) { return $Hint }
    $candidates = @(
        "${env:ProgramFiles}\Rhino 8\System\Rhino.exe",
        "${env:ProgramFiles(x86)}\Rhino 8\System\Rhino.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

if (Test-CassisMcp -Url $McpUrl) {
    Write-Host "Cassis MCP already up at $McpUrl"
    exit 0
}

Stop-StaleCassis

if (Test-CassisMcp -Url $McpUrl) {
    Write-Host "Cassis MCP already up at $McpUrl"
    exit 0
}

$RhinoPath = Resolve-RhinoPath -Hint $RhinoPath
if (-not $RhinoPath) {
    Write-Error "Rhino 8 not found. Set -RhinoPath or CASSIS_RHINO_PATH."
    exit 1
}

Write-Host "Starting Rhino + Grasshopper ($RhinoPath)…"
Start-Process -FilePath $RhinoPath -ArgumentList '/runscript="_Grasshopper"'

Write-Host "Waiting up to ${TimeoutSec}s for $McpUrl …"
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    if (Test-CassisMcp -Url $McpUrl) {
        Write-Host "Cassis MCP ready at $McpUrl"
        exit 0
    }
    Start-Sleep -Seconds 1
}

Write-Error @"
Timed out waiting for Cassis MCP.
Is Auto-start off? Place the Cassis component, click Start Server,
or enable 'Auto-start MCP when Grasshopper loads' in the component menu.
"@
exit 1
