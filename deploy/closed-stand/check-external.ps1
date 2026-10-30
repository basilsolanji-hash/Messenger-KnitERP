# External checks of the closed stand from a Windows computer (PowerShell 5.1+, built-in curl.exe).
# Run from the folder with server-endpoint.json:
#   powershell -ExecutionPolicy Bypass -File .\check-external.ps1 -Role operator
# Role is what THIS computer's public IP is in the allowlist: operator (operator + tester), tester (tester only), outsider.
param(
    [Parameter(Mandatory = $true)][ValidateSet('operator', 'tester', 'outsider')][string]$Role,
    [string]$Endpoint = (Join-Path $PSScriptRoot 'server-endpoint.json')
)
$ErrorActionPreference = 'Stop'
$e = Get-Content $Endpoint -Raw | ConvertFrom-Json
$origin = $e.origin.TrimEnd('/')
$hostName = ([Uri]$origin).Host
$ip = (Resolve-DnsName $hostName -Type A -DnsOnly | Where-Object { $_.Type -eq 'A' } | Select-Object -First 1).IPAddress
$empty = Join-Path $env:TEMP 'os-closed-empty.json'
[IO.File]::WriteAllText($empty, '{}')
$script:failed = 0

function Report($name, $actual, $expected) {
    $ok = $expected -contains "$actual"
    if (-not $ok) { $script:failed++ }
    '{0}  {1,-40} -> {2}  (expected {3})' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $actual, ($expected -join ' or ')
}
function Http($name, $expected, [string[]]$curlArgs) {
    $code = & curl.exe -s -o NUL -w '%{http_code}' --max-time 10 --path-as-is @curlArgs 2>$null
    Report $name "$code" $expected
}
function Post($name, $expected, $path) {
    Http $name $expected @('-X', 'POST', '-H', 'Content-Type: application/json', '--data-binary', "@$empty", "$origin$path")
}
function Port($port, $shouldBeOpen) {
    $client = New-Object Net.Sockets.TcpClient
    try { $open = $client.ConnectAsync($ip, $port).Wait(3000) } catch { $open = $false } finally { $client.Close() }
    Report "TCP $port from outside" $(if ($open) { 'open' } else { 'closed' }) @($(if ($shouldBeOpen) { 'open' } else { 'closed' }))
}

$myIp = & curl.exe -s --max-time 10 https://api.ipify.org
"Origin $origin -> $ip; this computer's public IP: $myIp; role: $Role"
$denied = @('403')
switch ($Role) {
    'operator' { $health = @('200'); $operator = @('400', '401'); $openapi = @('401', '404'); $user = @('400'); $hub = @('401') }
    'tester'   { $health = $denied;  $operator = $denied;        $openapi = $denied;        $user = @('400'); $hub = @('401') }
    'outsider' { $health = $denied;  $operator = $denied;        $openapi = $denied;        $user = $denied;  $hub = $denied }
}
Http 'GET  /health/ready' $health @("$origin/health/ready")
Http 'GET  /openapi/v1.json' $openapi @("$origin/openapi/v1.json")
Post 'POST /api/v1/dev/sms/code (no key)' $operator '/api/v1/dev/sms/code'
Post 'POST /api/v1/auth/sms/challenges {}' $user '/api/v1/auth/sms/challenges'
Post 'POST /hubs/events/negotiate (no token)' $hub '/hubs/events/negotiate'
if ($Role -ne 'operator') {
    # Operator routes must stay closed whatever the spelling: ASP.NET matches routes case-insensitively.
    foreach ($path in '/API/V1/DEV/sms/code', '/Api/v1/Dev/accounts', '/api//v1/dev/sms/code', '/api/v1/./dev/sms/code',
                      '/api/v1/x/../dev/sms/code', '/api/v1/%64ev/sms/code', '/api/v1/dev%2Fsms/code', '/HEALTH/ready') {
        Post "POST $path" $denied $path
    }
}
Http 'GET  / (unmapped)' @('404') @("$origin/")
Http 'correct leaf pin' @('404') @('--pinnedpubkey', ('sha256//' + $e.pins[0].Substring(7)), "$origin/")
Http 'wrong pin rejected' @('000') @('--pinnedpubkey', 'sha256//AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=', "$origin/")
Http 'plain HTTP port 80' @('000') @("http://${hostName}/")
Http 'plain HTTP to port 443' @('400') @("http://${hostName}:443/")
Http 'HTTPS by IP without hostname' @('000') @('-k', "https://$ip/")
Port 22 $true; Port 443 $true
foreach ($port in 80, 5080, 5432, 8080) { Port $port $false }
if ($script:failed) { "RESULT: $($script:failed) check(s) FAILED" } else { 'RESULT: ALL EXTERNAL CHECKS PASSED' }
