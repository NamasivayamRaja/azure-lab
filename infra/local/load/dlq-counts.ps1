# DLQ message count per entity, using the existing DlqTool "peek" (no new code).
# Run from d:\2026\AzureLabs:  .\infra\local\load\dlq-counts.ps1
$entities = 'redeem-requests', 'offer-notifications', 'document-processed', 'offer-events/ledger', 'offer-events/notification'
foreach ($e in $entities) {
    $out = dotnet run --project .\AzureLab.DlqTool --no-build -- peek $e 2>&1
    $n = ($out | Select-String -Pattern '^\s*#\d+').Count
    '{0,-30} DLQ={1}' -f $e, $n
}
