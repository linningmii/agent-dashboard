param([switch]$Clean)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$originalToken = $env:ENZYME_NPM_TOKEN
$originalConfig = $env:NPM_CONFIG_USERCONFIG
$temporaryConfig = Join-Path ([IO.Path]::GetTempPath()) ('agent-dashboard-npm-' + [guid]::NewGuid() + '.npmrc')
try {
  # Reuse the user's signed-in Entra session; never store or print the returned token.
  $env:ENZYME_NPM_TOKEN = (& az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv)
  if ($LASTEXITCODE -ne 0 -or -not $env:ENZYME_NPM_TOKEN) { throw 'Sign in with az login before installing from Enzyme.' }
  @('registry=https://o365exchange.pkgs.visualstudio.com/_packaging/Enzyme/npm/registry/', '//o365exchange.pkgs.visualstudio.com/_packaging/Enzyme/npm/registry/:_authToken=${ENZYME_NPM_TOKEN}', 'always-auth=true') | Set-Content -LiteralPath $temporaryConfig
  $env:NPM_CONFIG_USERCONFIG = $temporaryConfig
  $operation = if ($Clean) { 'ci' } else { 'install' }
  Push-Location -LiteralPath (Join-Path $projectRoot 'web')
  try {
    & npm.cmd $operation --ignore-scripts --fetch-retries=1 --fetch-timeout=30000 --loglevel=error
    if ($LASTEXITCODE -ne 0) { throw 'Enzyme package installation failed.' }
  } finally { Pop-Location }
} finally {
  $env:ENZYME_NPM_TOKEN = $originalToken
  $env:NPM_CONFIG_USERCONFIG = $originalConfig
  if (Test-Path -LiteralPath $temporaryConfig) { Remove-Item -LiteralPath $temporaryConfig }
}
