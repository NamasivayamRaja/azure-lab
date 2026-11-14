# Runs all invariant SQL files using a normal .NET connection string (no sqlcmd needed).
# Usage (from d:\2026\AzureLabs):
#   .\infra\local\load\run-invariants.ps1 -ConnectionString "Server=<server>;Database=<db>;Trusted_Connection=True;TrustServerCertificate=True;" [-SinceUtc "..."] [-FailOnViolation]
param(
    [Parameter(Mandatory)] [string] $ConnectionString,
    # Baseline cut-off: only rows created after this are checked (ignores old lab data). Example: "2026-11-07T10:30:00Z"
    [string] $SinceUtc = '2000-01-01T00:00:00Z',
    # CI/CD gate: exits with code 1 if any invariant is violated
    [switch] $FailOnViolation
)

$violations = New-Object System.Collections.Generic.List[string]

$conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
$conn.Open()
try {
    foreach ($f in Get-ChildItem (Join-Path $PSScriptRoot 'sql') -Filter *.sql | Sort-Object Name) {
        "`n===== $($f.Name) ====="
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = (Get-Content $f.FullName -Raw) -replace "'2000-01-01'", "'$SinceUtc'"
        $cmd.CommandTimeout = 60
        $ds = New-Object System.Data.DataSet
        $null = (New-Object System.Data.SqlClient.SqlDataAdapter $cmd).Fill($ds)   # one table per SELECT
        foreach ($t in $ds.Tables) {
            if ($t.Rows.Count -eq 0) { "(0 rows)"; "" }
            else { ($t | Select-Object * -ExcludeProperty RowError, RowState, Table, ItemArray, HasErrors | Format-Table -AutoSize | Out-String).TrimEnd(); "" }

            foreach ($r in $t.Rows) {
                foreach ($col in @('stuck_count', 'tx_without_inbox', 'inbox_without_tx', 'tx_with_wrong_event_count')) {
                    if ($t.Columns.Contains($col) -and [int]$r[$col] -gt 0) {
                        $violations.Add("$($f.Name): $col = $($r[$col])")
                    }
                }
                if ($t.Columns.Contains('invariant_status') -and $r['invariant_status'] -ne 'PASS') {
                    $violations.Add("$($f.Name): invariant_status = $($r['invariant_status'])")
                }
            }
        }
    }
}
finally { $conn.Dispose() }

"===== DLQ ====="
$dlqLines = & (Join-Path $PSScriptRoot 'dlq-counts.ps1')
$dlqLines
foreach ($line in $dlqLines) {
    if ($line -match 'DLQ=(\d+)' -and [int]$Matches[1] -gt 0) {
        $violations.Add("DLQ non-zero: $line")
    }
}

if ($FailOnViolation -and $violations.Count -gt 0) {
    Write-Error ("CI/CD Invariant Gate FAILED:`n - " + ($violations -join "`n - "))
    exit 1
}
