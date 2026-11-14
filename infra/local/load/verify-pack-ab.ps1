# Pack A & Pack B Verification Script
# Usage (from d:\2026\AzureLabs, after restarting API & Worker):
#   .\infra\local\load\verify-pack-ab.ps1 -ConnectionString $cs
param(
    [Parameter(Mandatory)] [string] $ConnectionString,
    [string] $ApiUrl = 'http://localhost:5215',
    [string] $WorkerUrl = 'http://localhost:5101'
)

"=== A3: Health Endpoints (API & Worker) ==="
foreach ($u in @("$ApiUrl/health", "$ApiUrl/health/live", "$ApiUrl/health/ready", "$WorkerUrl/health", "$WorkerUrl/health/live", "$WorkerUrl/health/ready")) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri $u -TimeoutSec 5
        "{0,-40} -> {1} ({2})" -f $u, [int]$r.StatusCode, $r.Content
    }
    catch {
        "{0,-40} -> FAILED ({1})" -f $u, $_.Exception.Message
    }
}

"`n=== B4: Prove IX_EventDocuments_BlobName_ETag Unique Index Exists & Rejects Duplicates (SQL 2601) ==="
$sql = @"
-- 1. Verify unique index metadata in sys.indexes
SELECT i.name AS index_name, i.is_unique,
       STRING_AGG(c.name + '(' + CAST(c.max_length AS varchar(10)) + 'B)', ', ') AS key_columns_bytes
FROM sys.indexes i
JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE i.object_id = OBJECT_ID('dbo.EventDocuments') AND i.name = 'IX_EventDocuments_BlobName_ETag'
GROUP BY i.name, i.is_unique;

-- 2. Prove duplicate (BlobName, ETag) insert raises SQL 2601 (the exact error caught by EventDocumentFunction)
BEGIN TRAN;
INSERT INTO dbo.EventDocuments (BlobName, ETag, Status, SizeBytes, ProcessedUtc)
VALUES ('pack-b4-test.pdf', '"etag-b4"', 'Accepted', 100, SYSDATETIMEOFFSET());

BEGIN TRY
    INSERT INTO dbo.EventDocuments (BlobName, ETag, Status, SizeBytes, ProcessedUtc)
    VALUES ('pack-b4-test.pdf', '"etag-b4"', 'Accepted', 100, SYSDATETIMEOFFSET());
    SELECT 'FAIL_NO_ERROR' AS duplicate_insert_test, 0 AS sql_error_number;
END TRY
BEGIN CATCH
    SELECT 'PASS_CAUGHT_DUPLICATE' AS duplicate_insert_test, ERROR_NUMBER() AS sql_error_number, ERROR_MESSAGE() AS sql_error_message;
END CATCH
ROLLBACK TRAN;
"@

$conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
$conn.Open()
try {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql
    $ds = New-Object System.Data.DataSet
    $null = (New-Object System.Data.SqlClient.SqlDataAdapter $cmd).Fill($ds)
    foreach ($t in $ds.Tables) {
        ($t | Format-Table -AutoSize | Out-String).TrimEnd()
        ""
    }
}
finally { $conn.Dispose() }
