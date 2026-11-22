# Cloud Week 2 — Day 5 Handoff (VNet, Private Endpoints, Private DNS & Network Break Drills)

Date: 2026-11-22 | Status: **DONE** | All 4 Must-Have Data/AI Services Private: **YES** *(keep AI Search + 6 Private Endpoints running through Day 6; delete billable extras tagged `expires: 2026-11-23` at Day 6 EOD)*

---

## 1. Resource Table — Private Endpoints, DNS Zones, Public Access & Daily Cost (`rg-azurelab-ndvd-dev2-cin`)

| Resource | Name | Region | Private Endpoint & IP | Sub-resource | Private DNS Zone | Public Access | Est. Cost / Day |
|---|---|---|---|---|---|---|---|
| **Storage (Blob)** | `stazurelabdevjyqvnq` | `centralindia` | `pe-st-azurelab-dev-cin-blob` (`10.10.1.5`) | `blob` | `privatelink.blob.core.windows.net` | **Disabled** | \$0.24 *(PE)* |
| **Storage (Queue)** | `stazurelabdevjyqvnq` | `centralindia` | `pe-st-azurelab-dev-cin-queue` (`10.10.1.4`) | `queue` | `privatelink.queue.core.windows.net` | **Disabled** | \$0.24 *(PE)* |
| **Storage (Table)** | `stazurelabdevjyqvnq` | `centralindia` | `pe-st-azurelab-dev-cin-table` (`10.10.1.6`) | `table` | `privatelink.table.core.windows.net` | **Disabled** | \$0.24 *(PE)* |
| **Azure SQL** | `sql-azurelab-dev-cin-jyqvnq` | `centralindia` | `pe-sql-azurelab-dev-cin` (`10.10.1.7`) | `sqlServer` | `privatelink.database.windows.net` | **Disabled** | \$0.24 *(PE)* |
| **Azure OpenAI** | `oai-azurelab-dev-sin-jyqvnq` | `southindia` | `pe-oai-azurelab-dev-cin` (`10.10.1.8`, cross-region in `centralindia`) | `account` | `privatelink.openai.azure.com` | **Disabled** | \$0.24 *(PE)* |
| **Azure AI Search** | `srch-azurelab-dev-cin-jyqvnq` | `centralindia` | `pe-srch-azurelab-dev-cin` (`10.10.1.9`) | `searchService` | `privatelink.search.windows.net` | **disabled** | \$2.42 *(Basic)* + \$0.24 *(PE)* |
| **VNet / Subnets / NSGs** | `vnet-azurelab-dev-cin` (`10.10.0.0/22`) | `centralindia` | `snet-appsvc-int` (`10.10.0.0/26`), `snet-pe` (`10.10.1.0/25`, `privateEndpointNetworkPolicies: Enabled`), `snet-aci` (`10.10.1.128/25`) | — | 6 linked zones (`registrationEnabled: false`) | Private | ~\$0.10 *(6 DNS zones @ \$0.50/mo)* |
| **API / Worker / Functions** | `asp-azurelab-dev-cin` (`P0v3`, 5 VNet-integrated targets incl. staging slots) | `centralindia` | Outbound via `snet-appsvc-int` (`outboundVnetRouting.applicationTraffic: true`) | — | — | **Public inbound (by design)** | ~\$2.40 *(dev2 baseline)* |
| **Service Bus** | `sb-azurelab-dev-cin-jyqvnq` (`Standard`) | `centralindia` | None (PE requires Premium ~\$677/mo) | — | — | **Public (keyless MI + RBAC)** | *(in baseline)* |
| **App Insights / Log Analytics** | `appi-azurelab-dev-cin` / `log-azurelab-dev-cin` | `centralindia` | None (AMPLS deferred — global DNS zone caveat) | — | — | **Public (Entra + TLS)** | *(in baseline)* |
| **Content Safety & Doc Intel** | `cs-azurelab-dev-sin-jyqvnq`, `di-azurelab-dev-cin-jyqvnq` | `southindia` / `centralindia` | Optional PEs deferred per Week 2 cut lines | — | — | **Enabled (keyless MI + RBAC)** | \$0.00/hr idle |

- **Total running cost/day (Days 5–6):** ~\$2.40 (baseline) + ~\$2.42 (AI Search Basic) + \$1.44 (6 PEs @ \$0.01/hr) + ~\$0.10 (6 DNS zones) ≈ **~\$6.36/day**.
- **Estimated credit remaining:** **~\$172.80 of \$200** (~19 days left; Day 5 start was \$173.69, Day 5 incremental ~\$0.90).
- **Teardown tag:** AI Search and all 6 PEs carry `expires: 2026-11-23` (delete at Day 6 EOD).

---

## 2. Interview DNS & Network Debugging Story

Full evidence table recorded in [docs/network-constraints.md](file:///d:/2026/AzureLabs/docs/network-constraints.md). Four live break drills established a deterministic 30-second triage tree:

1. **Public IP resolved + `403` / SQL public-access-denied → `DNS` (unlinked or wrong zone):** Unlinking `privatelink.blob.core.windows.net` caused API SSH to fall back to public IP `20.60.84.33` and hit Storage's shared public gateway (`HTTP 403`), mimicking an RBAC error until `remote_ip` exposed the public IP.
2. **`NXDOMAIN` (`Could not resolve host`) → `DNS` (missing `A` record inside a linked zone):** Deleting `pe-oai-azurelab-dev-cin`'s `dns-zone-group` while keeping `privatelink.openai.azure.com` linked caused **no public fallback** because the linked zone is authoritative inside the VNet (the classic hub-and-spoke DNS blackhole).
3. **`10.x` IP resolved + TCP timeout (`HTTP 000`) → `Filtering` (NSG) OR `Service setting` (Rejected/Pending PE):** Deleting `allow-appsvc-sql` on `nsg-snet-pe` (`privateEndpointNetworkPolicies: Enabled`) and setting `pe-srch-azurelab-dev-cin` to `Rejected` produced the exact same client symptom (`10.x` resolved, silent drop/timeout). Rejected PEs are terminal and require deleting + recreating via Bicep.
4. **`10.x` IP resolved + fast `403` → `Identity` (RBAC):** API SSH hitting `event-docs` returned `HTTP 403 remote=10.10.1.5` (least-privilege container RBAC), whereas `bulk-player-enrollment` returned `HTTP 200 remote=10.10.1.5`.

---

## 3. SQL Pipeline Decision & Rebuild Gap 1 Closure

- **Closed Week 1 Rebuild Gap 1:** Removed all 44 hardcoded `sqlAllowedIps` from [dev.bicepparam](file:///d:/2026/AzureLabs/infra/bicep/params/dev.bicepparam) (`param sqlAllowedIps = []`); apps reach SQL strictly over `pe-sql-azurelab-dev-cin` (`10.10.1.7`, `connectionType: Default` → Proxy TCP `1433`).
- **Pipeline Decision — Option A (VNet-injected ephemeral container / runner):**
  - Provisioned `snet-aci` (`10.10.1.128/25`, delegated `Microsoft.ContainerInstance/containerGroups`, \$0 idle) + `nsg-snet-pe` inbound allow TCP `1433` from `snet-aci` in [network.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/network.bicep).
  - Rejected **Option B** (temporary public window in `apps.yml`) for zero-public-exposure posture, and rejected **Option C** (Bicep `deploymentScripts`) because MS Learn confirms it requires `allowSharedKeyAccess: true` on its backing storage account (`403 KeyBasedAuthenticationNotPermitted`).
  - *Pending lab note:* [apps.yml](file:///d:/2026/AzureLabs/.github/workflows/apps.yml) still retains its Week 1 temporary firewall step (no prod deploy planned for this lab; wire ephemeral ACI / VNet runner if running `efbundle` in CI).

---

## 4. Outbound Routing & Full Private E2E Proof

- **Bicep / IaC:** Added [network.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/network.bicep) and [privateendpoint.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/privateendpoint.bicep); wired `virtualNetworkSubnetId` + `outboundVnetRouting: { applicationTraffic: true }` (`Microsoft.Web/sites@2024-11-01` replaced legacy `vnetRouteAllEnabled`) on all 5 targets (`api`, `api/staging`, `worker`, `func`, `func/staging` — VNet integration does not swap across slots).
- **Step 7 (`outboundVnetRouting`):** Verified `applicationTraffic: true`, while `imagePullTraffic`, `contentShareTraffic`, `backupRestoreTraffic`, `managedIdentityTraffic`, and `allTraffic` remain `false`.
- **Redeem E2E:** `POST /offers/5b0b2d4e-7c1a-4f0e-9a55-0a1b2c3d4e5f/redeem` completed (`status: 2`) across private SQL + Service Bus + Worker.
- **Private Ingestion + RAG `/ask` E2E:** Uploaded `day5-private-badge.png` from Functions SSH (`PUT 201 remote=10.10.1.5`), restarted `func-azurelab-dev-cin-jyqvnq` to recover the Blob Trigger listener backed off during the earlier Storage `403` break drill, and verified `POST /events/781ffe26-1158-4b9a-ae44-efe1f3541488/ask` returned `"answered": true`, `"answer": "The airport shuttle departs from Gate 4 [1]."` across private Storage, SQL, cross-region OpenAI, and AI Search.

---

## 5. What Day 6 Needs (API Management & AI Gateway)
1. Provision APIM in front of `app-azurelab-api-dev-cin-jyqvnq` (JWT validation, per-caller rate limits, backend Access Restrictions locking API to APIM).
2. Add AI Gateway policy (token limits + metrics) in front of `oai-azurelab-dev-sin-jyqvnq` (note: OpenAI currently has `publicNetworkAccess: Disabled`, so verify APIM tier / VNet connectivity or temporary exception before wiring).
3. **Day 6 EOD teardown:** Delete `srch-azurelab-dev-cin-jyqvnq` and the 6 Private Endpoints (`expires: 2026-11-23`) to drop daily burn back to baseline.
