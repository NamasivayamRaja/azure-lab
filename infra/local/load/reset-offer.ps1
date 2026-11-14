# Seeds or resets an OfferInventory row for Step 5 oversell tests.
# Usage: .\infra\local\load\reset-offer.ps1 -ConnectionString $cs [-OfferId "5b0b2d4e-7c1a-4f0e-9a55-0a1b2c3d4e5f"] [-Capacity 50]
param(
    [Parameter(Mandatory)] [string] $ConnectionString,
    [string] $OfferId = '5b0b2d4e-7c1a-4f0e-9a55-0a1b2c3d4e5f',
    [int] $Capacity = 50
)
$sql = @"
MERGE dbo.OfferInventories AS target
USING (SELECT CAST('$OfferId' AS uniqueidentifier) AS OfferId) AS src
ON target.OfferId = src.OfferId
WHEN MATCHED THEN
    UPDATE SET Capacity = $Capacity, Remaining = $Capacity, UpdatedUtc = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN
    INSERT (OfferId, Capacity, Remaining, UpdatedUtc)
    VALUES (src.OfferId, $Capacity, $Capacity, SYSDATETIMEOFFSET());

SELECT OfferId, Capacity, Remaining, UpdatedUtc FROM dbo.OfferInventories WHERE OfferId = '$OfferId';
"@
$conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
$conn.Open()
try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $ds = New-Object System.Data.DataSet
    $null = (New-Object System.Data.SqlClient.SqlDataAdapter $cmd).Fill($ds)
    $ds.Tables[0] | Format-Table -AutoSize
}
finally { $conn.Dispose() }
