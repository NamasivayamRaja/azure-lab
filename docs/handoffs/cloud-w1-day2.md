================================================================================
HANDOFF BLOCK FOR DAY 3 — CI/CD WITH OIDC, STAGING SLOTS, SWAP & ROLLBACK
================================================================================

1. ENVIRONMENT & GOVERNANCE (UNCHANGED)
- Subscription:        sub-azurelab-sandbox
- Resource Group:      rg-azurelab-ndvd-dev-cin
- Region:              centralindia (cin), suffix: 5kaayi
- Key Vault:           deployKeyVault=false (no real external secrets; all Azure auth is MI/Entra)
- AI:                  Ai__Enabled=false

2. IDENTITY & RBAC INVENTORY (POST-DAY 2 AUDIT)
- API Identity (app-azurelab-api-dev-cin-5kaayi):
  * Azure RBAC:        Storage Blob Delegator (scope: stazurelabdev5kaayi)
                       Storage Blob Data Contributor (scope: container bulk-player-enrollment)
  * SQL DB Roles:      EXTERNAL_USER in sqldb-azurelab-dev-cin -> db_datareader, db_datawriter
- API Staging Slot Identity (app-azurelab-api-dev-cin-5kaayi/slots/staging):
  * Azure RBAC:        NONE (grantStagingSlotRoles=false)
  * SQL DB Roles:      NONE (no EXTERNAL_USER created yet; /health=200, /health/ready=503 until Day 3)
- Worker Identity (app-azurelab-worker-dev-cin-5kaayi):
  * Azure RBAC:        Azure Service Bus Data Sender   (scope: queue redeem-requests)
                       Azure Service Bus Data Receiver (scope: queue redeem-requests)
                       Azure Service Bus Data Sender   (scope: topic offer-events)
                       Azure Service Bus Data Receiver (scope: subscription offer-events/ledger)
                       Azure Service Bus Data Receiver (scope: subscription offer-events/notification)
  * SQL DB Roles:      EXTERNAL_USER in sqldb-azurelab-dev-cin -> db_datareader, db_datawriter
- Function App Identity (func-azurelab-dev-cin-5kaayi):
  * Azure RBAC:        Storage Blob Data Contributor  (scope: stazurelabdev5kaayi)  [TIGHTENED from Owner]
                       Storage Queue Data Contributor (scope: stazurelabdev5kaayi)
                       Azure Service Bus Data Receiver (scope: queue offer-notifications)
                       Azure Service Bus Data Sender   (scope: queue document-processed)
                       Azure Service Bus Data Receiver (scope: queue document-processed)
  * SQL DB Roles:      EXTERNAL_USER in sqldb-azurelab-dev-cin -> db_datareader, db_datawriter
- Human Operator (ndvd):
  * Azure RBAC:        Owner (subscription scope); ad-hoc Storage Blob Data Contributor REMOVED
  * SQL Server:        Microsoft Entra Administrator on sql-azurelab-dev-cin-5kaayi
  * Firewall:          Temporary dev-client-ssms rule REMOVED

3. NEGATIVE-TEST RESULTS MATRIX (DAY 2 EVIDENCE)
| # | Identity & Role Removed | Scope | Propagation | Triggered Path | Observed Status / Error | Where Surfaced |
|---|---|---|---|---|---|---|
| 1 | API: Storage Blob Delegator | stazurelabdev5kaayi | >200s + restart (2h key cache) | GET /events/{id}/documents/{f}/download-sas | HTTP 500 (403 AuthorizationPermissionMismatch); CSV upload still 201 | API response + API logs |
| 2 | API: Storage Blob Data Contributor | container bulk-player-enrollment | ~56s | POST upload + curl existing SAS URI | Upload HTTP 500; existing SAS HTTP 403 | API response + StorageBlobLogs |
| 3 | Worker: Azure Service Bus Data Sender | queue redeem-requests | ~60s + restart | POST /offers/{id}/redeem | API 202 Accepted; Outbox RetryCount=5, ProcessedUtc=NULL, UnauthorizedAccessException | dbo.OutboxMessages + Worker logs |
| 4 | Worker: Azure Service Bus Data Receiver | sub offer-events/ledger | Cached >60s (AMQP link) | POST /offers/{id}/redeem | Consumed while AMQP link/RBAC cache was still warm; requires >180s + restart | Service Bus broker |
| 5 | Functions: Storage Blob Data Owner | stazurelabdev5kaayi | ~180s + restart | GET /api/health + upload to event-docs | /api/health 503; blob upload 201 (silent), 0 SQL rows until restored (~5m catchup) | HTTP 503 + StorageBlobLogs (03b5463a... 403) |
| 6 | Functions: Azure Service Bus Data Receiver | queue document-processed | ~180s + restart | Upload valid PDF to event-docs | Queued in document-processed until restored; DLQ=4 after restore | Service Bus + Function host |
| 7 | API: SQL db_datawriter | sqldb-azurelab-dev-cin | 0s (immediate) | GET /health/ready + POST /offers/{id}/redeem | /health/ready 200 OK; POST /redeem HTTP 500 (SQL Error 229 INSERT denied) | API response + SQL |

4. BICEP & LIVE AZURE CHANGES APPLIED
- infra/bicep/modules/roleassignments.bicep:
  * Downgraded func-azurelab-dev-cin-5kaayi from Storage Blob Data Owner to Storage Blob Data Contributor.
  * Deleted legacy Storage Blob Data Owner role assignment (ARM Incremental mode does not delete omitted assignments).
- Removed manual Storage Blob Data Contributor from ndvd on stazurelabdev5kaayi.
- Verified what-if drift check: 0 creates, 0 modifies, 0 deletes (3 ignored: master DB + 2 action groups; 1 ! diagnosticSettings).

5. PENDING CODE / CONFIG FOR LATER DAYS
- Production code still uses new DefaultAzureCredential() in:
  * AzureLabs.Api/Storage/BlobStorageServiceCollectionExtensions.cs
  * AzureLab.Messaging/ServiceBusRegistration.cs
  * AzureLab.Functions/Program.cs
  -> Proposal: switch to ManagedIdentityCredential() in !IsDevelopment() and AzureCliCredential() in Development.
- SQL DB roles on all 3 app MIs are still DB-wide db_datareader + db_datawriter (tighten to per-table grants if desired).
- Subscription Activity Log diagnostic setting is not yet wired to log-azurelab-dev-cin (needed for AzureActivity KQL + Day 5 alerts).

6. WHAT DAY 3 NEEDS (CI/CD OIDC, STAGING SLOTS & SWAP)
- Minimum roles for the GitHub Actions OIDC deploy identity (User-Assigned MI or App Registration federated credential):
  * Contributor on rg-azurelab-ndvd-dev-cin (deploy Bicep + Web Apps/Functions + slot swap)
  * Role Based Access Control Administrator (or User Access Administrator with condition) on rg-azurelab-ndvd-dev-cin so Bicep can create/update roleassignments.bicep
  * Do NOT grant Owner at subscription scope to CI/CD.
- Staging slot warm-up: decide whether to set grantStagingSlotRoles=true + create SQL EXTERNAL_USER for app-azurelab-api-dev-cin-5kaayi/slots/staging so /health/ready passes on staging before swap.
================================================================================
