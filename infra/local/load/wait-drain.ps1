# Polls SQL until every redeem transaction since -SinceUtc is final AND the outbox has no unsent rows.
# Prints the backlog every 10 s and the total DRAIN TIME (the real end-to-end latency of the pipeline under load).
# Usage: .\infra\local\load\wait-drain.ps1 -SinceUtc "<utc time the k6 run started>" -ConnectionString "..." [-TimeoutMinutes 30]
param(
    [Parameter(Mandatory)] [string] $ConnectionString,
    [Parameter(Mandatory)] [string] $SinceUtc,
    [int] $TimeoutMinutes = 30
)
$sql = @"
SELECT
 (SELECT COUNT(*) FROM dbo.RedeemTransactions WHERE RequestedAtUtc >= '$SinceUtc')                       AS total_tx,
 (SELECT COUNT(*) FROM dbo.RedeemTransactions WHERE RequestedAtUtc >= '$SinceUtc' AND Status IN (0,1))  AS not_final,
 (SELECT COUNT(*) FROM dbo.OutboxMessages WHERE CreatedUtc >= '$SinceUtc' AND ProcessedUtc IS NULL AND RetryCount < 5) AS outbox_unsent
"@
$conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
$conn.Open()
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    while ($true) {
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql
        $r = $cmd.ExecuteReader(); $null = $r.Read()
        $total = $r['total_tx']; $nf = $r['not_final']; $un = $r['outbox_unsent']; $r.Close()
        '{0,6:N0}s  total_tx={1}  not_final={2}  outbox_unsent={3}' -f $sw.Elapsed.TotalSeconds, $total, $nf, $un
        if ($nf -eq 0 -and $un -eq 0 -and $total -gt 0) { "DRAINED in {0:N0}s" -f $sw.Elapsed.TotalSeconds; break }
        if ($sw.Elapsed.TotalMinutes -gt $TimeoutMinutes) { "TIMEOUT: still not drained (stuck rows?)"; break }
        Start-Sleep -Seconds 10
    }
}
finally { $conn.Dispose() }
