# Step 7: Demonstrates:
#   1. Retry Storm Amplification (Chaos:Mode = "ThrowStorm": 4 inner tries x 3 Service Bus deliveries = 12 attempts per message -> 240 attempts for 20 requests!)
#   2. Messages landing in the Dead-Letter Queue (DLQ = 20)
#   3. The DLQ -> Monitor -> Replay / Reconcile recovery path:
#      - Monitor: dlq-counts.ps1 shows redeem-requests DLQ = 20
#      - Fix outage: Chaos:Mode = "None"
#      - Replay: DlqTool resubmit-all redeem-requests replays all 20 messages -> 0 DLQ, 0 stuck, all 20 final!
#
# Usage (from d:\2026\AzureLabs):
#   .\infra\local\load\test-retry-storm.ps1 -ConnectionString $cs
param(
    [Parameter(Mandatory)] [string] $ConnectionString
)

$root = Resolve-Path "$PSScriptRoot\..\..\.."
$chaosPath = Join-Path $root 'chaos.json'

"=== 1. Reset offer capacity to 50 and enable Chaos:Mode = ThrowStorm (4x3 = 12x amplification) ==="
& "$PSScriptRoot\reset-offer.ps1" -ConnectionString $ConnectionString -Capacity 50
Set-Content -Path $chaosPath -Value '{"Chaos":{"Mode":"ThrowStorm","SlowSeconds":45}}'
Start-Sleep -Seconds 1

$start7 = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
"=== 2. Firing 20 redeem requests via k6 (20 requests x 12 attempts = 240 handler executions -> 20 DLQ) ==="
k6 run -e VUS=10 -e ITERS=20 "$PSScriptRoot\k6\storm-burst.js"

"Waiting 12s for outbox relay + 3 Service Bus deliveries to exhaust into DLQ..."
Start-Sleep -Seconds 12

"=== 3. MONITOR: Check DLQ counts and stuck Pending transactions BEFORE recovery ==="
& "$PSScriptRoot\dlq-counts.ps1"

$conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
$conn.Open()
try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT Status, COUNT(*) AS cnt FROM dbo.RedeemTransactions WHERE RequestedAtUtc >= '$start7' GROUP BY Status ORDER BY Status;"
    $ds = New-Object System.Data.DataSet
    $null = (New-Object System.Data.SqlClient.SqlDataAdapter $cmd).Fill($ds)
    "`nRedeemTransactions status during outage (0 = Pending):"
    $ds.Tables[0] | Format-Table -AutoSize
}
finally { $conn.Dispose() }

"=== 4. RECOVER: Turn Chaos OFF (outage resolved) and replay DLQ via DlqTool resubmit-all ==="
Set-Content -Path $chaosPath -Value '{"Chaos":{"Mode":"None","SlowSeconds":45}}'
Start-Sleep -Seconds 1

dotnet run --project "$root\AzureLab.DlqTool" --no-build -- resubmit-all redeem-requests

"=== 5. Wait for drain and verify all invariants pass ==="
& "$PSScriptRoot\wait-drain.ps1" -SinceUtc $start7 -ConnectionString $ConnectionString
& "$PSScriptRoot\run-invariants.ps1" -SinceUtc $start7 -ConnectionString $ConnectionString
