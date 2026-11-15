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


## Day 3 — GitHub Actions CI/CD with OIDC, Staging Slots & Safe Releases

### Decisions
- **GitHub Free (Public Repo `NamasivayamRaja/azure-lab`)**: Branch ruleset on `main` (PR required, 0 approvals for solo dev, `build-and-whatif` status check, no force push) + `dev` (unprotected) and `prod` (required reviewer) Environments.
- **Deploy Identity**: Single UAMI `id-azurelab-github-dev-cin` at RG scope (`Contributor` + `Role Based Access Control Administrator`) for Day 3 velocity; documented two-identity split (`infra` vs `app`) for production.
- **Worker Strategy**: Kept Worker slotless (`appservice.bicep`)—relies on `SIGTERM` graceful shutdown, Service Bus peek-lock expiry, 30s Outbox leases, and idempotent handlers.
- **Functions Staging Slot**: Added `staging` slot to `functionapp.bicep` with sticky `AzureWebJobs.<Function>.Disabled=true` and host-storage RBAC (`Storage Blob Data Contributor` + `Storage Queue Data Contributor`), stopping `staging` slots after each swap.
- **Plan SKU**: Upgraded `dev.bicepparam` from `S1` (1.75 GB, ~\$2.28/day) to `P0v3` (4 GB, ~\$2.04/day) and enabled `WEBSITE_RUN_FROM_PACKAGE=1`.
- **DB Migrations**: Self-contained `efbundle` run by `id-azurelab-github-dev-cin` (`Authentication=Active Directory Default`) before app deploys, using a short-lived runner IP firewall rule cleaned up in `if: always()`.

### Key Commands Run
- OIDC bootstrap: `az identity create`, `az identity federated-credential create/update`, `az role assignment create`.
- SQL deploy user: `CREATE USER [id-azurelab-github-dev-cin] FROM EXTERNAL PROVIDER` + `db_ddladmin`, `db_datareader`, `db_datawriter`.
- Slot & rollback tests: `az webapp start/stop`, `az webapp deployment slot swap`, `az deployment group create` (drift restore).

### Issues & Fixes
- **Immutable OIDC IDs**: GitHub sent `repo:NamasivayamRaja@10979213/azure-lab@1457712472:...`; updated all 3 federated credentials to match exact casing and `@<id>` suffixes (`AADSTS700213`).
- **OneDeploy 504 Timeout**: `S1` SMB extraction took ~4m49s, hitting Azure ARR's ~230s gateway timeout; fixed with `WEBSITE_RUN_FROM_PACKAGE=1`, `P0v3`, and sequential slot swaps.
- **Functions `0 functions found` (404)**: `actions/upload-artifact@v4` strips hidden folders (`.azurefunctions/`) by default; fixed with `include-hidden-files: true`.
- **Functions Staging `AuthorizationPermissionMismatch`**: Staging slot MI lacked `Storage Blob/Queue Data Contributor` on `AzureWebJobsStorage`; added `functionStagingPrincipalId` to `roleassignments.bicep`.
- **`efbundle` Login Failed (18456)**: Added `EXTERNAL_USER` for `id-azurelab-github-dev-cin` inside `sqldb-azurelab-dev-cin` and `--verbose` + `Connection Timeout=120`.


## Day 4 — Real Authentication & Authorization on the API (2026-11-15)

### Decisions
- **In-code JWT Bearer (`Microsoft.Identity.Web`) over Easy Auth**: Chosen for local dev parity, ASP.NET Core policy flexibility (`ApiAccess` supporting `scp` or `roles`), per-event authorization, and future OBO support.
- **v2 Tokens & `Audience`**: Configured `requestedAccessTokenVersion = 2` on `api-azurelab-dev`; omitted `AzureAd:Audience` so `Microsoft.Identity.Web` accepts the bare `ClientId` GUID emitted in v2 `aud` claims.
- **Public Repo Hygiene**: Kept real `TenantId`/`ClientId` out of `appsettings.json` and `dev.bicepparam` (using `dotnet user-secrets` locally and `readEnvironmentVariable` + GitHub Repo Variables in CI/CD).
- **Authorization Model**: Removed `UserId` from `RedeemRequestDto` (derived from token `oid` to eliminate BOLA/IDOR); added `EventEnrollment` table (`EventId`, `UserId`) for per-event ACLs on `/ask` and `/assistant`; kept `EventDocumentsController` gated by `ApiAccess` only for Operator/employee uploads.

### Key Commands Run
- Entra ID app registrations: `az ad app create`, `az ad sp create`, `az rest --method PATCH https://graph.microsoft.com/v1.0/applications/$API_OBJ_ID` (scopes, `Api.Service` app role, `preAuthorizedApplications`).
- Token acquisition (zero secrets): `az account get-access-token --scope "api://$API_APP_ID/api.access"`.
- EF migration & local secrets: `dotnet ef migrations add AddEventEnrollments`, `dotnet user-secrets set "AzureAd:*"`.
- Local & Azure curl/PowerShell test matrix (`401`, `200`, `403`, `503`).

### Issues & Fixes
- **Graph `InvalidValue` / `InvalidAppId`**: `preAuthorizedApplications` cannot be added in the same `PATCH` that creates the scope, and `04b07795-8ddb-461a-bbee-02f9e1bf7b46` is the real Microsoft Azure CLI App ID. Split into two `PATCH` calls.
- **`401` on valid v2 token**: `AzureAd:Audience = api://<id>` rejected v2 tokens whose `aud` is the bare `<id>`; removed `Audience` from config and Bicep.
- **Bicep linter `no-hardcoded-env-urls`**: Replaced `'https://login.microsoftonline.com/'` with `environment().authentication.loginEndpoint` in `appservice.bicep`.
- **Azure `IDW10106: The 'ClientId' option must be provided`**: `infra.yml` was missing `AZURE_TENANT_ID` and `AZURELAB_API_CLIENT_ID` in `env:`; added both so `dev.bicepparam` populates `AzureAd__*` on App Service.
