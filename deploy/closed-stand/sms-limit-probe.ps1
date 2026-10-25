# Negative check: a forged X-Forwarded-For must not bypass the per-IP SMS limit (5 per 10 minutes).
# Sends 6 synthetic Fake-SMS challenges, each with a different forged X-Forwarded-For. Expected: 5 accepted, 6th 429.
# Uses up this IP's SMS quota for 10 minutes (and 6 of 30 per day): phones behind the same IP must wait before registering.
#   powershell -ExecutionPolicy Bypass -File .\sms-limit-probe.ps1
param([string]$Endpoint = (Join-Path $PSScriptRoot 'server-endpoint.json'))
$ErrorActionPreference = 'Stop'
$origin = (Get-Content $Endpoint -Raw | ConvertFrom-Json).origin.TrimEnd('/')
# Synthetic P-256 public key (SubjectPublicKeyInfo); its private key was discarded. Not a real device.
$authKey = 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEVHf6FxQHHb0Om8EdGIjmqSrOz5lm5xhbciodDZInTYhZ2iMb/lqC3OW8nEcOst7dDmrfZw5Zq0FR3ngxXmLFIA=='
$rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
# Counters use fixed 10-minute windows; do not start the series right before a window boundary.
$second = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() % 600
if ($second -gt 560) { "Waiting $(605 - $second) s for a fresh 10-minute window..."; Start-Sleep -Seconds (605 - $second) }
$codes = @()
for ($i = 1; $i -le 6; $i++) {
    $identity = New-Object byte[] 33; $rng.GetBytes($identity); $identity[0] = 5
    $body = [ordered]@{
        requestId = [guid]::NewGuid().ToString(); phone = '+7999' + (Get-Random -Minimum 1000000 -Maximum 9999999)
        deviceId = [guid]::NewGuid().ToString(); registrationId = Get-Random -Minimum 1 -Maximum 16383
        identityKey = [Convert]::ToBase64String($identity); authKey = $authKey
    } | ConvertTo-Json -Compress
    $file = Join-Path $env:TEMP 'os-closed-sms-probe.json'; [IO.File]::WriteAllText($file, $body)
    $out = Join-Path $env:TEMP 'os-closed-sms-probe.out'
    $forged = "198.51.100.$i"
    $code = & curl.exe -s -o $out -w '%{http_code}' --max-time 15 -X POST -H 'Content-Type: application/json' `
        -H "X-Forwarded-For: $forged" -H "X-Real-IP: $forged" --data-binary "@$file" "$origin/api/v1/auth/sms/challenges"
    $problem = ''
    if ($code -ne '200') { try { $problem = (Get-Content $out -Raw | ConvertFrom-Json).code } catch { } }
    "request $i  X-Forwarded-For: $forged  -> $code $problem"
    $codes += "$code"
}
Remove-Item -ErrorAction SilentlyContinue (Join-Path $env:TEMP 'os-closed-sms-probe.*')
if (($codes[0..4] | Where-Object { $_ -ne '200' }).Count -eq 0 -and $codes[5] -eq '429') {
    'RESULT: PASS - forged X-Forwarded-For did not reset the per-IP limit.'
    'Next, on the server: python3 deploy/closed-stand/ip-counters.py YOUR_IP 172.18.0.1 198.51.100.1 198.51.100.6'
} else { 'RESULT: FAIL - expected 5 x 200 then 429. If an earlier run was within 10 minutes, wait and repeat.' }
