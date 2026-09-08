# Starts the dashboard in a hidden background process. Tunnel hosting is handled by the server.
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nodePath = (Get-Command node.exe -ErrorAction Stop).Source
$entryPath = Join-Path $projectRoot 'src/server.mjs'
Push-Location -LiteralPath $projectRoot
try {
  $dashboardConfig = & $nodePath --input-type=module -e "import {config} from './src/config.mjs'; console.log(JSON.stringify({port:config.port}));"
} finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'Dashboard configuration could not be loaded.' }
$dashboardPort = ($dashboardConfig | ConvertFrom-Json).port
try {
  $existing = Invoke-RestMethod -Uri "http://127.0.0.1:$dashboardPort/api/status" -TimeoutSec 10
  if ($null -ne $existing.minimumRunning -and $null -ne $existing.sources) {
    Write-Output "Agent Dashboard is already running at http://127.0.0.1:$dashboardPort."
    return
  }
} catch { }
$dataPath = Join-Path $projectRoot 'data'
New-Item -ItemType Directory -Force -Path $dataPath | Out-Null
$dashboardProcess = Start-Process -FilePath $nodePath -ArgumentList @('--experimental-sqlite', ('"{0}"' -f $entryPath)) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dataPath 'server.log') -RedirectStandardError (Join-Path $dataPath 'server-error.log')
$dashboardProcess.Id | Set-Content -LiteralPath (Join-Path $projectRoot '.agent-dashboard.pid')
Write-Output "Started Agent Dashboard (PID $($dashboardProcess.Id))."
Write-Output "Tunnel status: http://127.0.0.1:$dashboardPort/api/tunnels"
