param([string]$BaseUrl = 'http://127.0.0.1:4317', [string]$AccessKeyFile = 'data/ui-access-key', [switch]$Remote)
$ErrorActionPreference = 'Stop'
$key = (Get-Content -LiteralPath $AccessKeyFile -Raw).Trim()
$adminHeaders = @{ Authorization = "Bearer $key"; Accept = 'application/json' }
$tunnels = Invoke-RestMethod "$BaseUrl/api/tunnels" -Headers $adminHeaders -TimeoutSec 30
$uiUrl = $BaseUrl
$ingestionUrl = 'http://127.0.0.1:4319'
$deviceHeaders = @{ Accept = 'application/json' }
if ($Remote) {
  if ($tunnels.ui.state -ne 'hosting' -or $tunnels.ingestion.state -ne 'hosting') { throw 'Both tunnels must be hosting.' }
  $uiUrl = $tunnels.ui.url.TrimEnd('/')
  $ingestionUrl = $tunnels.ingestion.url.TrimEnd('/')
  foreach ($url in @($uiUrl, $ingestionUrl)) {
    if ($url -notmatch '^https://[a-z0-9-]+\.[a-z0-9]+\.devtunnels\.ms$') { throw 'Unexpected tunnel URL.' }
  }
  $uiToken = & devtunnel token $tunnels.ui.id --scopes connect --json | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { throw 'UI tunnel authentication failed.' }
  $deviceToken = & devtunnel token $tunnels.ingestion.id --scopes connect --json | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { throw 'Ingestion tunnel authentication failed.' }
  $adminHeaders['X-Tunnel-Authorization'] = 'tunnel ' + $uiToken.token
  $deviceHeaders['X-Tunnel-Authorization'] = 'tunnel ' + $deviceToken.token
}
function Request-Json([string]$Url, [string]$Method, [hashtable]$Headers, $Body = $null) {
  $args = @{ Uri=$Url; Method=$Method; Headers=$Headers; TimeoutSec=45; ContentType='application/json' }
  if ($null -ne $Body) { $args.Body = $Body | ConvertTo-Json -Depth 20 -Compress }
  Invoke-RestMethod @args
}
function Expect-Status([string]$Url, [int[]]$Statuses, [hashtable]$Headers = @{}) {
  $handler = [Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false
  $client = [Net.Http.HttpClient]::new($handler)
  foreach ($entry in $Headers.GetEnumerator()) { $client.DefaultRequestHeaders.Add($entry.Key,$entry.Value) }
  try { $response = $client.GetAsync($Url).GetAwaiter().GetResult(); $code = [int]$response.StatusCode }
  finally { if($response){$response.Dispose()}; $client.Dispose() }
  if ($code -notin $Statuses) { throw "Unexpected HTTP $code for $Url" }
}
Expect-Status "$uiUrl/api/status" @(302,303,307,401,403)
$baseline = Request-Json "$uiUrl/api/status" GET $adminHeaders
$page = Invoke-WebRequest "$uiUrl/" -Headers $adminHeaders -TimeoutSec 30
if ($page.Content -notmatch 'type="module"') { throw 'React build not served.' }
$pair = Request-Json "$uiUrl/api/devices/pair" POST $adminHeaders @{}
$registered = Request-Json "$ingestionUrl/v1/devices/register" POST $deviceHeaders @{code=$pair.code; name='Temporary .NET migration verification'}
$deviceHeaders.Authorization = 'Bearer ' + $registered.token
$completionId = $null
try {
  Expect-Status "$ingestionUrl/api/status" @(404) $deviceHeaders
  Expect-Status "$uiUrl/health" @(404) $adminHeaders
  $session = Request-Json "$ingestionUrl/v1/devices/$($registered.deviceId)/sessions" POST $deviceHeaders @{}
  $report = @{version=1;sessionId=$session.sessionId;sequence=1;sources=@{codex=@{available=$true;automatic=$true;detail='Fixture'}};tasks=@(@{id='test-turn';source='codex';title='Temporary deployment verification';status='running';startedAt=[DateTimeOffset]::UtcNow.ToString('o');latestOutput='Checking full deployment'});completions=@()}
  Request-Json "$ingestionUrl/v1/devices/$($registered.deviceId)/snapshot" PUT $deviceHeaders $report | Out-Null
  $running = Request-Json "$uiUrl/api/status" GET $adminHeaders
  if (-not ($running.tasks | Where-Object {$_.deviceId -eq $registered.deviceId -and $_.status -eq 'running'})) { throw 'Remote task not aggregated.' }
  $report.sequence = 2; $report.tasks = @(); $report.completions = @(@{id='complete-event';taskId='test-turn';source='codex';title='Temporary deployment verification';status='completed';completedAt=[DateTimeOffset]::UtcNow.ToString('o');latestOutput='Verified final output'})
  Request-Json "$ingestionUrl/v1/devices/$($registered.deviceId)/snapshot" PUT $deviceHeaders $report | Out-Null
  $finished = Request-Json "$uiUrl/api/status" GET $adminHeaders
  $completion = $finished.completions | Where-Object {$_.deviceId -eq $registered.deviceId}
  if ($completion.latestOutput -ne 'Verified final output') { throw 'Completion output not preserved.' }
  $completionId = $completion.id
  Request-Json ("$uiUrl/api/completions/" + [uri]::EscapeDataString($completionId)) DELETE $adminHeaders | Out-Null
  $report.sequence = 3
  Request-Json "$ingestionUrl/v1/devices/$($registered.deviceId)/snapshot" PUT $deviceHeaders $report | Out-Null
  $after = Request-Json "$uiUrl/api/status" GET $adminHeaders
  if ($after.completions | Where-Object {$_.deviceId -eq $registered.deviceId}) { throw 'Acknowledged event reappeared.' }
  Add-Type -AssemblyName System.Net.Http
  $http = [System.Net.Http.HttpClient]::new()
  foreach ($entry in $adminHeaders.GetEnumerator()) { $http.DefaultRequestHeaders.Add($entry.Key,$entry.Value) }
  $cancel = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(50))
  try {
    $response = $http.GetAsync("$uiUrl/api/events",[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cancel.Token).GetAwaiter().GetResult()
    $response.EnsureSuccessStatusCode() | Out-Null
    $reader = [IO.StreamReader]::new($response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
    $snapshotCount = 0; $sawHeartbeat = $false
    while (-not ($snapshotCount -ge 2 -or ($snapshotCount -ge 1 -and $sawHeartbeat))) {
      $line = $reader.ReadLineAsync($cancel.Token).GetAwaiter().GetResult()
      if ($null -eq $line) { throw 'SSE closed early.' }
      if ($line -eq 'event: snapshot') { $snapshotCount++ }
      if ($line -eq ': keep-alive') { $sawHeartbeat = $true }
    }
    Write-Output 'Authenticated React UI, listener separation, device reporting, completion/replay, and live SSE: passed.'
  } finally { $cancel.Cancel(); if($reader){$reader.Dispose()}; if($response){$response.Dispose()}; $http.Dispose(); $cancel.Dispose() }
} finally {
  if ($completionId) { Request-Json ("$uiUrl/api/completions/" + [uri]::EscapeDataString($completionId)) DELETE $adminHeaders | Out-Null }
  Request-Json "$uiUrl/api/devices/$($registered.deviceId)" DELETE $adminHeaders | Out-Null
}
Write-Output "UI: $uiUrl"
Write-Output "Ingestion: $ingestionUrl"
