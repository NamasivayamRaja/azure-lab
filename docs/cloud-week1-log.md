# Cloud Week 1 Log

## Day 2 — Managed Identity, RBAC Proof & Negative Tests (2026-11-14)

### Decisions
- **Functions host storage role**: Downgraded `func-azurelab-dev-cin-5kaayi` from `Storage Blob Data Owner` to `Storage Blob Data Contributor` in [roleassignments.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/roleassignments.bicep); verified `/api/health` and `BlobTrigger` quarantine copy/delete both succeed.
- **Credential strategy (proposed, pending code edit)**: Use `ManagedIdentityCredential` in `!IsDevelopment()` and `AzureCliCredential` in `Development` instead of `DefaultAzureCredential` to eliminate stray env-var hijacking and dev-tool fallback latency.
- **Key Vault**: Remains off (`deployKeyVault=false`); Storage (`allowSharedKeyAccess=false`), Service Bus (`disableLocalAuth=true`), and SQL (`azureAdOnlyAuthentication=true`) have zero keys/passwords.

### Key Commands Run
- Identity & RBAC inventory: `az webapp identity show`, `az functionapp identity show`, `az role assignment list --all`, `az role definition list`.
- Negative tests: `az role assignment delete` per role + `az webapp/functionapp restart`, followed by `az deployment group create -g rg-azurelab-ndvd-dev-cin -f infra/bicep/main.bicep -p infra/bicep/params/dev.bicepparam`.
- MI token inspection via `az webapp ssh`: decoded JWT claims (`aud: https://storage.azure.com/`, `oid: 3652e8bd-8b7c...`, `idtyp: app`).
- Detection queries: `az monitor log-analytics query` on `StorageBlobLogs` (`StatusCode == 403`) and `az monitor activity-log list` for `roleAssignments`.
- Drift check: `az deployment group what-if` after removing temporary `dev-client-ssms` SQL firewall rule.

### Issues & Fixes
- **Service Bus role names**: Built-in roles are prefixed with `Azure ` (`Azure Service Bus Data Sender` / `Receiver`), not `Service Bus Data Sender`.
- **API CSV vs. Functions PDF**: API `DocumentFileRules` accepts `.csv` only (`bulk-player-enrollment`); Functions `EventDocumentFunction` accepts `.pdf/.png/.jpg/.docx` (`event-docs`).
- **ARM Incremental mode**: Deploying tightened Bicep added `Storage Blob Data Contributor` without removing the old `Storage Blob Data Owner`; deleted the old assignment manually and verified via `az role assignment list`.
- **RBAC propagation vs. AMQP/key caches**: Storage/SB RBAC revocations took ~56s–200s+ to propagate, and `UserDelegationSasService` caches keys for 2h (requires app restart to observe immediately); SQL role removal took 0s.
