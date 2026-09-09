param([string]$BaseUrl = 'http://127.0.0.1:4317', [string]$AccessKeyFile = 'data/ui-access-key')
$ErrorActionPreference = 'Stop'
$headers = @{}
$auth = Invoke-RestMethod "$BaseUrl/api/auth/status"
if ($auth.requiresLogin) { $key = (Get-Content -LiteralPath $AccessKeyFile -Raw).Trim(); $headers.Authorization = "Bearer $key" }
$document = Invoke-RestMethod "$BaseUrl/openapi/v1.json" -Headers $headers
# OpenAPI is generated from the API's typed endpoint signatures. Omit machine-specific origins.
$document.servers = @()
New-Item -ItemType Directory -Force contracts | Out-Null
$document | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath contracts/openapi.json -Encoding utf8
Write-Output 'Exported contracts/openapi.json'
