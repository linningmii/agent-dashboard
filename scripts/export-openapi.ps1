param([string]$BaseUrl = 'http://127.0.0.1:4417', [string]$AccessKeyFile = 'data/migration-preview/ui-access-key')
$ErrorActionPreference = 'Stop'
$key = (Get-Content -LiteralPath $AccessKeyFile -Raw).Trim()
$document = Invoke-RestMethod "$BaseUrl/openapi/v1.json" -Headers @{Authorization = "Bearer $key"}
# OpenAPI is generated from the API's typed endpoint signatures. Omit machine-specific origins.
$document.servers = @()
New-Item -ItemType Directory -Force contracts | Out-Null
$document | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath contracts/openapi.json -Encoding utf8
Write-Output 'Exported contracts/openapi.json'
