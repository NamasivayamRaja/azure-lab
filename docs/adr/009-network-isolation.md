# ADR 009: Network Isolation, Private Endpoints, Private DNS, and Private SQL Migrations

- **Status:** Accepted (Validated on Cloud Week 2, Day 5 & Day 7; PEs toggled off between sessions to save credit)
- **Date:** 2026-11-26
- **Context:** After proving identity-only (`disableLocalAuth: true`) access publicly on Days 1–4, all stateful data and AI endpoints (`Storage`, `SQL`, `Azure OpenAI`, `AI Search`) must be isolated from the public internet (`publicNetworkAccess: Disabled`) inside `vnet-azurelab-dev-cin` (`10.10.0.0/22`), while preserving CI/CD EF Core database migrations and staying within the remaining Free Trial budget.

## Decision
1. **VNet Topology & Subnet Segmentation (`vnet-azurelab-dev-cin`, `10.10.0.0/22` in `centralindia`):**
   - `snet-appsvc-int` (`10.10.0.0/26`, delegated `Microsoft.Web/serverFarms`, `nsg-snet-appsvc-int`): Outbound VNet Integration (`outboundVnetRouting: { applicationTraffic: true }`) explicitly attached to all 5 compute targets (`api`, `api/staging`, `worker`, `func`, `func/staging`, since VNet integration does not swap across slots).
   - `snet-pe` (`10.10.1.0/25`, `privateEndpointNetworkPolicies: 'Enabled'`, `nsg-snet-pe`): Hosts all Private Endpoint NICs with explicit inbound allow rules only for `snet-appsvc-int` (TCP `443`, `1433`), `snet-aci` (TCP `1433`), and `snet-apim-int` (TCP `443`), plus `deny-other-vnet` (`priority: 4000`).
   - `snet-aci` (`10.10.1.128/25`, delegated `Microsoft.ContainerInstance/containerGroups`): Hosts ephemeral CI migration containers.
   - `snet-apim-int` (`10.10.2.0/26`, delegated `Microsoft.Web/serverFarms`, `Microsoft.Web` Service Endpoint, `nsg-snet-apim-int`): Outbound VNet integration for APIM Standard v2.
2. **Six Private Endpoints + Six Linked Private DNS Zones (`publicNetworkAccess: Disabled`):**
   - **Storage (`stazurelabdevjyqvnq`):** Three separate Private Endpoints and DNS zones — `blob`, `queue`, **and** `table` (`privatelink.{blob,queue,table}.core.windows.net`) — because Azure Functions host storage (`AzureWebJobsStorage`) requires all three sub-resources for triggers, leases, and locks.
   - **Azure SQL (`sql-azurelab-dev-cin-jyqvnq`):** `pe-sql-azurelab-dev-cin` (`sqlServer` → `privatelink.database.windows.net`), clearing all 44 `sqlAllowedIps` (`param sqlAllowedIps = []`).
   - **Azure OpenAI (`oai-azurelab-dev-sin-jyqvnq` in `southindia`):** Cross-region Private Endpoint `pe-oai-azurelab-dev-cin` deployed in `centralindia` (`account` → `privatelink.openai.azure.com`), traversing the Microsoft backbone privately across regions.
   - **Azure AI Search (`srch-azurelab-dev-cin-jyqvnq`):** `pe-srch-azurelab-dev-cin` (`searchService` → `privatelink.search.windows.net`).
3. **Keyless Pipeline Path to Private SQL (Ephemeral ACI in `snet-aci`):**
   - `.github/workflows/apps.yml` (authenticated via GitHub OIDC as `id-azurelab-github-dev-cin`, holding `Contributor` on `dev2` RG + `Managed Identity Operator` on itself) spawns an ephemeral container `aci-ef-migrate-<run_id>` in `snet-aci` with `--assign-identity id-azurelab-github-dev-cin`, runs `dotnet ef database update` over `10.10.1.x:1433` using Entra MI (`sp_getapplock '__EFMigrationsLock'` + EF `ExecutionStrategy` retry for Serverless `Error 40613`), and deletes the container in an `if: always()` step (<\$0.01/run, \$0 idle).
4. **Scoped Exclusions (Identity-Locked Public Endpoints in Dev):**
   - **Service Bus stays `Standard` (`disableLocalAuth: true`, Managed Identity RBAC):** Service Bus Private Endpoints require the **Premium** SKU (~\$0.928/MU/hr ≈ **\$677/mo** vs ~\$10/mo Standard).
   - **Content Safety & Document Intelligence (`S0`):** Optional Private Endpoints omitted in dev per Week 2 cut lines to save PE hourly costs; locked down with `disableLocalAuth: true` and least-privilege RBAC.

## Alternatives Considered
- **Option A — Temporary public SQL firewall window in `apps.yml`:** Rejected. Re-enabling `publicNetworkAccess` during deploys violates zero-public-exposure governance (blocked if an Azure Policy `Deny` rule is active) and leaves SQL exposed if a workflow crashes mid-run.
- **Option B — Self-hosted GitHub runner VM in `vnet-azurelab-dev-cin`:** Rejected for dev lab. Costs ~\$15–\$40+/mo 24/7 plus OS patching and runner registration upkeep, versus <\$0.01/run for ephemeral ACI.
- **Option C — Bicep `Microsoft.Resources/deploymentScripts` for SQL migrations:** Rejected. Requires `allowSharedKeyAccess: true` on its backing Storage account (`403 KeyBasedAuthenticationNotPermitted` when shared keys are disabled) and couples app schema changes to infra deployments.
- **Option D — Run `Database.MigrateAsync()` at API/Worker startup:** Rejected. Requires granting runtime apps `db_ddladmin` (violating least privilege) and races across multiple instances and staging/production slots.

## Trade-offs Accepted & Consequences
- **Positive:** Eliminates public data-plane exposure on Storage, SQL, OpenAI, and AI Search; closes Week 1 Rebuild Gap 1 (`sqlAllowedIps = []`); deterministic 4-branch DNS/network triage runbook (`docs/network-constraints.md`).
- **Negative / Trade-offs:**
  - 6 Private Endpoints cost ~\$0.06/hr (~\$1.44/day, ~\$43/mo) plus ~\$3/mo for 6 Private DNS zones (mitigated in dev by toggling `deploy*PrivateEndpoint = false` between sessions).
  - Ephemeral ACI adds ~60–90s to migration runs (container pull + restore).

## Revisit When
- Compliance or production transition mandates private networking for Service Bus (upgrade `Standard` → `Premium` ~\$677/mo and attach `pe-sb`), Content Safety, Document Intelligence, and Azure Monitor (`AMPLS`).
- CI deployment frequency exceeds ~10–20 runs/day or repo becomes private (move from ephemeral ACI `git clone` to an ACR-hosted `efbundle` image or a VNet-injected Container Apps Job self-hosted runner pool).
