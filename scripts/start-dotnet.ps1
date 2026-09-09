param([switch]$Collector, [int]$Port = 4317, [int]$IngestionPort = 4319)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$hubDll = Join-Path $root 'artifacts/release/hub/Dashboard.Api.dll'
$collectorDll = Join-Path $root 'artifacts/release/collector/Dashboard.Collector.dll'
if (-not (Test-Path -LiteralPath $hubDll)) { throw 'Build first with scripts/build.ps1.' }
$hubRunning = $false
try {
  $status = Invoke-RestMethod "http://127.0.0.1:$Port/api/auth/status" -TimeoutSec 3
  if ($status.requiresLogin -is [bool] -and $status.authenticated -is [bool]) { $hubRunning = $true; Write-Output 'The .NET API service is already running.' }
} catch { }
$logs = Join-Path $root 'data'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
if (-not $hubRunning) {
  $server = Start-Process -FilePath $dotnet -ArgumentList @(('"{0}"' -f $hubDll),'--ui-port',$Port,'--ingestion-port',$IngestionPort) -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'hub.log') -RedirectStandardError (Join-Path $logs 'hub-error.log')
  $server.Id | Set-Content -LiteralPath (Join-Path $logs 'hub.pid')
  Write-Output "API started: PID $($server.Id), http://127.0.0.1:$Port"
}
if ($Collector) {
  if (-not (Test-Path -LiteralPath (Join-Path $root 'collector.json'))) { throw 'Enroll this device before starting its collector.' }
  if (-not (Test-Path -LiteralPath $collectorDll)) { throw 'Published collector not found. Build first.' }
  $pidFile = Join-Path $logs 'collector.pid'
  if (Test-Path -LiteralPath $pidFile) {
    $savedId = [int](Get-Content -LiteralPath $pidFile)
    $existing = Get-CimInstance Win32_Process -Filter "ProcessId = $savedId" -ErrorAction SilentlyContinue
    if ($existing -and $existing.CommandLine.Contains($collectorDll)) { Write-Output 'The collector is already running.'; return }
  }
  $worker = Start-Process -FilePath $dotnet -ArgumentList @(('"{0}"' -f $collectorDll),'run') -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'collector.log') -RedirectStandardError (Join-Path $logs 'collector-error.log')
  $worker.Id | Set-Content -LiteralPath (Join-Path $logs 'collector.pid')
  Write-Output "Separate collector started: PID $($worker.Id)"
}
