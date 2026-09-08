param([string]$Output = 'artifacts/release')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $root
try {
  dotnet restore AgentDashboard.slnx --configfile NuGet.Config --artifacts-path artifacts/windows
  if ($LASTEXITCODE -ne 0) { throw '.NET restore failed' }
  dotnet test AgentDashboard.slnx --no-restore --artifacts-path artifacts/windows --configuration Release --verbosity minimal
  if ($LASTEXITCODE -ne 0) { throw '.NET tests failed' }
  Push-Location -LiteralPath web
  try {
    node node_modules/typescript/bin/tsc -p tsconfig.json
    if ($LASTEXITCODE -ne 0) { throw 'TypeScript check failed' }
    node node_modules/vite/bin/vite.js build
    if ($LASTEXITCODE -ne 0) { throw 'UI build failed' }
  } finally { Pop-Location }
  dotnet publish src/Dashboard.Api --no-restore --artifacts-path artifacts/windows -c Release -o "$Output/hub"
  if ($LASTEXITCODE -ne 0) { throw 'API publish failed' }
  dotnet publish src/Dashboard.Collector --no-restore --artifacts-path artifacts/windows -c Release -o "$Output/collector"
  if ($LASTEXITCODE -ne 0) { throw 'Collector publish failed' }
} finally { Pop-Location }
