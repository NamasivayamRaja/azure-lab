================================================================================
HANDOFF BLOCK FOR DAY 2 — MANAGED IDENTITY, RBAC PROOF & NEGATIVE TESTS
================================================================================

1. ENVIRONMENT & GOVERNANCE
- Subscription:        sub-azurelab-sandbox (Owner verified)
- Resource Group:      rg-azurelab-ndvd-dev-cin
- Region:              centralindia (short code: cin)
- Unique Suffix:       5kaayi (derived from uniqueString(resourceGroup().id))
- Tags Applied:        workload=azurelab, environment=dev, owner=ndvd, managedBy=bicep
- Policy Guardrail:    allowed-locations-cin (Allowed locations = centralindia)
- Budget Guardrail:    budget-azurelab-week1 (₹3,000/month; 50/80/100% actual + 100% forecast)

2. PROVISIONED AZURE RESOURCES
- App Service Plan:    asp-azurelab-dev-cin (Linux S1, 1 instance)
- API Web App:         app-azurelab-api-dev-cin-5kaayi (DOTNETCORE|10.0)
- API Staging Slot:    app-azurelab-api-dev-cin-5kaayi/slots/staging (DOTNETCORE|10.0)
- Worker Web App:      app-azurelab-worker-dev-cin-5kaayi (DOTNETCORE|10.0)
- Function App:        func-azurelab-dev-cin-5kaayi (DOTNET-ISOLATED|10.0)
- Storage Account:     stazurelabdev5kaayi (StorageV2, Standard_LRS, allowSharedKeyAccess=false)
  * Containers:        bulk-player-enrollment, event-docs, event-docs-quarantine,
                       azure-webjobs-hosts, azure-webjobs-secrets
- Service Bus:         sb-azurelab-dev-cin-5kaayi (Standard, disableLocalAuth=true)
  * Queues:            redeem-requests (DLQ on expiry=true, lock=30s),
                       offer-notifications (DLQ on expiry=false, lock=1m),
                       document-processed (DLQ on expiry=false, lock=1m; 3 AiDisabled messages in DLQ)
  * Topic:             offer-events
  * Subscriptions:     ledger (rule: $Default 1=1),
                       notification (rule: offer-redeemed-filter, CorrelationFilter label='OfferRedeemed')
- Azure SQL Server:    sql-azurelab-dev-cin-5kaayi.database.windows.net (Entra-only auth, TLS 1.2)
  * Firewall:          app-outbound-0..app-outbound-30 (31 App Service Plan outbound IPs; no 0.0.0.0 rule)
- Azure SQL Database:  sqldb-azurelab-dev-cin (GP_S_Gen5_1 Serverless, minCap=0.5, autoPause=15m,
                       useFreeLimit=true, exhaustion=BillOverUsage, backup=Local, 9 EF migrations applied)
- Observability:       log-azurelab-dev-cin (PerGB2018, 1GB/day cap, 30d) + appi-azurelab-dev-cin

3. CURRENT PERMISSIONS MATRIX (AZURE RBAC + SQL)
- API Identity (app-azurelab-api-dev-cin-5kaayi):
  * Azure RBAC:        Storage Blob Delegator (scope: stazurelabdev5kaayi)
                       Storage Blob Data Contributor (scope: container bulk-player-enrollment)
  * SQL DB Roles:      EXTERNAL_USER in sqldb-azurelab-dev-cin -> db_datareader, db_datawriter
- API Staging Slot Identity (app-azurelab-api-dev-cin-5kaayi/slots/staging):
  * Azure RBAC:        NONE (grantStagingSlotRoles=false)
  * SQL DB Roles:      NONE (no EXTERNAL_USER created yet)
- Worker Identity (app-azurelab-worker-dev-cin-5kaayi):
  * Azure RBAC:        Service Bus Data Sender   (scope: queue redeem-requests)
                       Service Bus Data Receiver (scope: queue redeem-requests)
                       Service Bus Data Sender   (scope: topic offer-events)
                       Service Bus Data Receiver (scope: subscription offer-events/ledger)
                       Service Bus Data Receiver (scope: subscription offer-events/notification)
  * SQL DB Roles:      EXTERNAL_USER in sqldb-azurelab-dev-cin -> db_datareader, db_datawriter
- Function App Identity (func-azurelab-dev-cin-5kaayi):
  * Azure RBAC:        Storage Blob Data Owner         (scope: stazurelabdev5kaayi)
                       Storage Queue Data Contributor  (scope: stazurelabdev5kaayi)
                       Service Bus Data Receiver       (scope: queue offer-notifications)
                       Service Bus Data Sender         (scope: queue document-processed)
                       Service Bus Data Receiver       (scope: queue document-processed)
  * SQL DB Roles:      EXTERNAL_USER in sqldb-azurelab-dev-cin -> db_datareader, db_datawriter
- Human Operator (ndvd):
  * Azure RBAC:        Owner (subscription scope)
                       Storage Blob Data Contributor (scope: stazurelabdev5kaayi — added during Step 6.4 CLI upload test)
  * SQL Server:        Microsoft Entra Administrator on sql-azurelab-dev-cin-5kaayi

4. INTENTIONAL OVER-PERMISSIONS (TO REVIEW / TIGHTEN IN DAY 2)
- SQL Database Roles (API, Worker, Functions):
  * All three identities currently hold database-wide `db_datareader` and `db_datawriter` (grants SELECT/INSERT/UPDATE/DELETE on every table in `dbo`, including `__EFMigrationsHistory`), rather than per-table object grants (e.g., API only needs INSERT on `OutboxMessages`; Functions only touches `OfferNotificationLogs`, `ProcessedMessages`, and `EventDocuments`).
- Function App Storage Scope:
  * `func-azurelab-dev-cin-5kaayi` holds `Storage Blob Data Owner` at the entire Storage Account scope (required by the Functions host for `azure-webjobs-hosts` and `azure-webjobs-secrets`), which also grants it full access to `bulk-player-enrollment` even though its business logic only uses `event-docs` and `event-docs-quarantine`.
- Human Operator Data-Plane Role:
  * Your user principal currently has `Storage Blob Data Contributor` on `stazurelabdev5kaayi` from Step 6.4 testing.

5. INTENTIONAL GAPS & READY-MADE TARGETS FOR DAY 2 RBAC NEGATIVE TESTING
- Gap 1 (API -> Service Bus Direct Access):
  * `apiDevEndpointsEnabled = false` in `dev.bicepparam`. The API identity has ZERO Service Bus roles (it writes only to SQL `OutboxMessages`). Any direct Service Bus call from the API (such as `/sb/ping`, `/dev/sb/malformed`, or `/admin/dlq/resubmit`) is blocked by both environment gating and RBAC (`UnauthorizedAccessException`).
- Gap 2 (API -> `event-docs` / `event-docs-quarantine` Containers):
  * The API identity's `Storage Blob Data Contributor` role is scoped strictly to container `bulk-player-enrollment`. Attempting to read/write blobs in `event-docs` or `event-docs-quarantine` using the API identity will fail with `403 AuthorizationPermissionMismatch`.
- Gap 3 (Staging Slot Identity -> Zero RBAC & Zero SQL User):
  * `app-azurelab-api-dev-cin-5kaayi/slots/staging` has its own System-Assigned Managed Identity with zero Storage/Service Bus RBAC roles (`grantStagingSlotRoles=false`) and no SQL `EXTERNAL_USER`. Hitting `/health` on staging succeeds (`200 OK`), while `/health/ready` on staging fails (`503 Unhealthy` SQL login failure) until provisioned for Day 3 slot swap.
- Gap 4 (SQL DDL Prevention):
  * None of the three app identities holds `db_ddladmin` or `db_owner`. Any attempt by an app identity to execute `CREATE TABLE`, `DROP TABLE`, or `ALTER ROLE` fails with SQL permission denied (`Msg 262 / 15247`).
- Pending Code Fix for Next API Deploy:
  * Remove the legacy `if (!blobClient.CanGenerateSasUri)` guard in `AzureLabs.Api/Storage/EventDocumentStore.cs` (lines 67–70) so `GetDownloadSasAsync` delegates to `UserDelegationSasService.CreateAsync` just like `GetUploadSasAsync`.
================================================================================