param([switch]$Clean, [ValidateSet('auto','npmjs','enzyme')][string]$Registry, [switch]$UpdateLock)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$arguments = @('--experimental-strip-types', (Join-Path $root 'scripts/install-web.ts'))
if ($Registry) { $arguments += @('--registry', $Registry) }
if ($UpdateLock) { $arguments += '--update-lock' }
# Clean is accepted for backward compatibility; locked npm ci is now the default.
& node @arguments
if ($LASTEXITCODE -ne 0) { throw 'Web dependency installation failed.' }
