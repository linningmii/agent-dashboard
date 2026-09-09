param([switch]$CollectorOnly)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$components = if ($CollectorOnly) { @('collector') } else { @('collector','hub') }
foreach ($component in $components) {
  $pidFile = Join-Path $root "data/$component.pid"
  if (-not (Test-Path -LiteralPath $pidFile)) { continue }
  $savedId = [int](Get-Content -LiteralPath $pidFile)
  $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $savedId" -ErrorAction SilentlyContinue
  if (-not $processInfo) { continue }
  $relativeEntry = if ($component -eq 'hub') { 'artifacts/release/hub/Dashboard.Api.dll' } else { 'artifacts/release/collector/Dashboard.Collector.dll' }
  $entry = Join-Path $root $relativeEntry
  if ($processInfo.Name -ne 'dotnet.exe' -or -not $processInfo.CommandLine.Contains($entry)) { throw "Saved $component PID belongs to another process; refusing to stop it." }
  $children = if ($component -eq 'hub') { @(Get-CimInstance Win32_Process -Filter "Name = 'devtunnel.exe'" | Where-Object {$_.ParentProcessId -eq $savedId}) } else { @() }
  Stop-Process -Id $savedId
  foreach ($child in $children) { Stop-Process -Id $child.ProcessId -ErrorAction SilentlyContinue }
  Write-Output "Stopped $component (PID $savedId)."
}
