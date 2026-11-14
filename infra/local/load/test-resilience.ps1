# Step 6: Demonstrates Polly v8 Standard Resilience Pipeline on:
#   1. redeem-loopback (AttemptTimeout 3s, Retry 2x + jitter, CircuitBreaker 15s, TotalTimeout 10s) when API is slow
#   2. ollama-embedding / ollama-chat when Ollama is stopped (Retry + jitter -> CircuitBreaker fast-fail in <20ms)
#
# Usage (from d:\2026\AzureLabs):
#   .\infra\local\load\test-resilience.ps1 -ConnectionString $cs
param(
    [Parameter(Mandatory)] [string] $ConnectionString,
    [string] $BaseUrl = 'http://localhost:5215'
)

function New-TestProposal([string] $userId = '12345') {
    $prid = [Guid]::NewGuid()
    $oid = [Guid]::NewGuid()
    $eventid ='F878DD09-415E-48EA-A4E6-9091A29FCE80'
    $sql = @"
INSERT INTO dbo.RedeemProposals (Id, UserId, OfferId, EventId, Status, CreatedUtc, ExpiresUtc, CorrelationId)
VALUES ('$prid', '$userId', '$oid', '$eventid', 'Pending', SYSDATETIMEOFFSET(), DATEADD(MINUTE, 10, SYSDATETIMEOFFSET()), 'resilience-test');
"@
    $conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql
        $null = $cmd.ExecuteNonQuery()
    }
    finally { $conn.Dispose() }
    return $prid
}

function Invoke-Timed([scriptblock] $Call) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $r = & $Call
        $sw.Stop()
        return [pscustomobject]@{ Status = [int]$r.StatusCode; ElapsedMs = $sw.ElapsedMilliseconds }
    }
    catch {
        $sw.Stop()
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
        return [pscustomobject]@{ Status = $code; ElapsedMs = $sw.ElapsedMilliseconds }
    }
}

$chaosPath = Join-Path (Resolve-Path "$PSScriptRoot\..\..\..") 'chaos.json'

"=== PART A: redeem-loopback with API Slow (5s > 3s AttemptTimeout) ==="
$p1 = New-TestProposal
$p2 = New-TestProposal
Set-Content -Path $chaosPath -Value '{"Chaos":{"Mode":"ApiSlow","SlowSeconds":5}}'
Start-Sleep -Seconds 1   # let IConfiguration reload chaos.json

$r1 = Invoke-Timed { Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$BaseUrl/proposals/$p1/confirm" -Headers @{ 'X-Dev-User' = '12345' } }
"Call 1 (3 attempts x 3s AttemptTimeout + jitter -> opens CircuitBreaker): Status=$($r1.Status)  Elapsed=$($r1.ElapsedMs) ms"

$r2 = Invoke-Timed { Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$BaseUrl/proposals/$p2/confirm" -Headers @{ 'X-Dev-User' = '12345' } }
"Call 2 (CircuitBreaker is OPEN -> fails fast with BrokenCircuitException!): Status=$($r2.Status)  Elapsed=$($r2.ElapsedMs) ms"

Set-Content -Path $chaosPath -Value '{"Chaos":{"Mode":"None","SlowSeconds":45}}'
"Waiting 16s for CircuitBreaker BreakDuration (15s) to close..."
Start-Sleep -Seconds 16

$p3 = New-TestProposal
$r3 = Invoke-Timed { Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$BaseUrl/proposals/$p3/confirm" -Headers @{ 'X-Dev-User' = '12345' } }
"Call 3 (API healthy again, Circuit Half-Open -> Closed):                  Status=$($r3.Status)  Elapsed=$($r3.ElapsedMs) ms"

"`n=== PART B: Ollama client (run with Ollama stopped to see Retry -> CircuitBreaker fast-fail) ==="
$askUri = "$BaseUrl/events/f878dd09-415e-48ea-a4e6-9091a29fce80/ask"
$body = '{"question":"What is the refund policy?"}'
for ($i = 1; $i -le 3; $i++) {
    $ra = Invoke-Timed { Invoke-WebRequest -UseBasicParsing -Method Post -Uri $askUri -ContentType 'application/json' -Body $body }
    "Ollama Ask #$i : Status=$($ra.Status)  Elapsed=$($ra.ElapsedMs) ms"
}
