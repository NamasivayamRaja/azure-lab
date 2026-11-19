================================================================================
HANDOFF BLOCK FOR WEEK 2 (DAY 0) — CONSOLIDATION, REBUILD PROOF & TEARDOWN COMPLETE
================================================================================

1. CURRENT AZURE STATE (POST-TEARDOWN & REBUILD)
- Subscription:        sub-azurelab-sandbox (Free Trial / Credit: ~$182–183 of $200 remaining; ~22–23 days left; $14.75 billed + ~$1–3 recent load lag)
- Deleted Group:       rg-azurelab-ndvd-dev-cin (deleted in 3m 08s; 0 public IPs, 0 soft-deleted Key Vaults/Cognitive accounts, 0 orphaned RBAC assignments)
- Active Workload RG:  rg-azurelab-ndvd-dev2-cin (centralindia, suffix: jyqvnq)
  * App Service Plan:  asp-azurelab-dev-cin (Linux P0v3, autoscale 1..2)
  * API Web App:       app-azurelab-api-dev-cin-jyqvnq (+ staging slot, stopped)
  * Worker Web App:    app-azurelab-worker-dev-cin-jyqvnq (slotless)
  * Function App:      func-azurelab-dev-cin-jyqvnq (+ staging slot, stopped)
  * Storage Account:   stazurelabdevjyqvnq (Standard_LRS, allowSharedKeyAccess=false)
  * Service Bus:       sb-azurelab-dev-cin-jyqvnq (Standard, disableLocalAuth=true)
  * SQL Server/DB:     sql-azurelab-dev-cin-jyqvnq.database.windows.net / sqldb-azurelab-dev-cin (GP_S_Gen5_1 Serverless, Entra-only)
  * Observability:     log-azurelab-dev-cin (1 GB/day cap, 30d) + appi-azurelab-dev-cin + alerts + autoscale
- Active Shared RG:    rg-azurelab-ndvd-shared-cin (purpose=identity-bootstrap)
  * Deploy Identity:   id-azurelab-github-dev-cin (UAMI, clientId: 5e8c36f9-2978-4875-bdca-2ea2f316e0b2, principalId: 7bbea270-ae3b-4672-8f32-844495631a95)
  * Federated Creds:   gh-pr-whatif, gh-env-dev, gh-env-prod (bound to immutable repo/owner IDs @10979213/@1457712472)
  * RBAC on dev2 RG:   Contributor + Role Based Access Control Administrator
- Entra ID (Global):   api-azurelab-dev (kept; survived rebuild with zero rework)
- Budget Guardrail:    budget-azurelab-week1 (₹3,000/month at subscription scope; 50/80/100% actual + 100% forecast; verify full email address in Portal)

2. REBUILD TEST RESULTS & TIMING
- Infra Pass 1 (new RG):       3m 20s ARM deploy (4m 11s wall clock)
- Infra Pass 2 (new SQL IPs):  2m 17s ARM deploy (2m 46s wall clock) -> ~7m total infra deploy
- PR Checks (what-if + build): 49s (what-if) + 1m 32s (apps build) -> Passed
- Pipeline Merge Deploy:       infra.yml + apps.yml (efbundle + slot swaps) -> Succeeded
- E2E Verification on dev2:    /health/ready = 200, /staging = 403 (stopped), POST /redeem = 202 -> Status 2 (Completed), Remaining 50 -> 49, DLQ = 0, what-if drift = 0.

3. REBUILD GAPS DISCOVERED (MANUAL STEPS OUTSIDE BICEP/PIPELINE)
1. **Two-pass SQL Outbox IPs (`sqlAllowedIps`):** New App Service Plan outbound IPs do not exist until Pass 1 completes; `dev.bicepparam` requires capture + Pass 2 redeploy (eliminated in Week 2 Day 5 via VNet Integration + Private Endpoints).
2. **SQL `EXTERNAL USER` Grants for System-Assigned MIs:** Bicep cannot execute T-SQL `CREATE USER ... FROM EXTERNAL PROVIDER`; rebuilding mints new system-assigned principals requiring manual SSMS/scripted grants (`db_datareader`, `db_datawriter`).
3. **Code/Schema Deployment:** Bicep provisions empty hosts and an empty database; `apps.yml` (`efbundle` + zip deploy) must run before `/health/ready` succeeds.
4. **Temporary SSMS Client Firewall Rule:** Manual IP rule needed to run initial SQL user grants, then deleted to keep `what-if` drift-free.
5. **CI/CD Deploy UAMI Lifecycle Coupling:** Originally placed inside the workload RG; deleting the RG destroyed the CI identity. Resolved by moving `id-azurelab-github-dev-cin` to `rg-azurelab-ndvd-shared-cin`.
6. **Hardcoded Resource Names in Workflows:** `apps.yml` and `infra.yml` hardcoded RG and `5kaayi` suffix names; required a repo edit to `jyqvnq` / `dev2`. Fix long-term by reading Bicep deployment outputs or GitHub Environment variables.
7. **Seed / Reference Data:** `efbundle` creates schema only; `OfferInventories` had to be seeded manually before the first E2E redeem test.
8. **Portal-Created `AlertBudget` Action Group:** Existed in old RG outside Bicep (unused by the subscription budget, which emailed directly). Subscription guardrails must never depend on workload-RG resources.

4. REPO HYGIENE, ADRs & MOCK INTERVIEW STATUS
- **Secret Scan:** 0 secrets/keys/passwords in git history or tracked files; `apiDevEndpointsEnabled = false` in dev & prod params.
- **Added Artifacts:**
  * Root [README.md](file:///d:/2026/AzureLabs/README.md) with Quick Start and Mermaid Azure deployment diagram.
  * 5 one-page ADRs in `docs/adr/` (`001-identity-and-rbac.md` through `005-scaling-and-failure-handling.md`).
- **Mock Interview (Step 4):** **PENDING** — 10 questions (including 3 whiteboard scenarios) appended to [docs/interview-qa.md](file:///d:/2026/AzureLabs/docs/interview-qa.md) for self-practice.

5. WEEK 2 READINESS & BICEP / QUOTA PLAN
- **Bicep Changes Needed for Week 2 (`WEEK2-PLAN.md`):**
  * **Day 1:** `infra/bicep/modules/openai.bicep` (Azure OpenAI / Foundry account with `disableLocalAuth: true`, model deployments for chat + embeddings) + `Cognitive Services OpenAI User` role in `roleassignments.bicep`.
  * **Day 2:** `infra/bicep/modules/search.bicep` (Azure AI Search **Basic** tier or higher—Free tier lacks semantic ranker, MI auth, and Private Endpoints; `disableLocalAuth: true`) + `Search Index Data Contributor` / `Search Index Data Reader` roles.
  * **Day 3–4:** Content Safety (Prompt Shields) & Document Intelligence Bicep resources (`disableLocalAuth: true`) + `Cognitive Services User` roles.
  * **Day 5:** `infra/bicep/modules/network.bicep` (VNet, App Service VNet Integration subnet, Private Endpoint subnet, NSGs, Private DNS Zones + VNet links for `privatelink.blob.core.windows.net`, `privatelink.database.windows.net`, `privatelink.openai.azure.com`, `privatelink.search.windows.net`, and `publicNetworkAccess: 'Disabled'` flags).
  * **Service Bus SKU Note:** Add `serviceBusSku` parameter if moving to `Premium` for Private Endpoints in production; **dropped as a live lab in Week 2** due to ~\$677/mo cost (answer from design notes).
- **Day 0 Gate Results (Captured 2026-11-19):**
  * **Resource Providers:** Registered `Microsoft.CognitiveServices` and `Microsoft.Search` on `sub-azurelab-sandbox` (both were previously `NotRegistered`).
  * **Azure AI Search:** `Central India` confirmed in `Microsoft.Search/searchServices` supported locations.
  * **Azure OpenAI / Foundry:** `az cognitiveservices usage list` returned active `OpenAI.*` quota entries in `southindia` and `eastus2` (exact `Standard`/`GlobalStandard` TPM limits for chat + embedding models in `centralindia` vs `southindia`/`eastus2` to be unwrapped at start of Week 2 Day 0/1; note `allowed-locations-cin` policy currently restricts to `centralindia` if a secondary region is needed).
  * **Next Quick Check (Week 2 Day 0 start):**
    ```powershell
    foreach ($loc in @('centralindia', 'southindia', 'eastus2')) {
      "=== $loc ==="
      (az cognitiveservices usage list -l $loc | ConvertFrom-Json) |
        Where-Object { $_.name.value -match 'OpenAI\.(Standard|GlobalStandard)\.(gpt-4o|text-embedding)' -and $_.limit -gt 0 } |
        Select-Object @{n='Model';e={$_.name.value}}, currentValue, limit | Format-Table -AutoSize
    }
    ```
- **Remaining Credit Plan (~$182 left, ~22–23 days):**
  * **Baseline (`P0v3` + SB Standard + SQL Serverless):** ~\$2.40/day (~\$55 over 23 days).
  * **Day 1 (Azure OpenAI):** Pay-as-you-go token billing (~\$0.50–\$2.00 for lab + evals; idle = \$0).
  * **Day 2 (AI Search Basic):** ~\$75/mo (~\$0.10/hr -> **create late, delete same day** = <\$0.50).
  * **Day 3–4 (Content Safety + Doc Intelligence):** Pay-per-call free/S0 tiers (~\$0.50–\$1.50 total).
  * **Day 5 (Private Endpoints + Private DNS):** ~\$0.01/hr per PE + \$0.50/DNS zone/mo (~5 PEs + 4 zones ≈ \$2.50/day -> delete PEs or RG after Day 6).
  * **Projected Week 2 Total:** ~\$35–\$45 total, leaving >\$135 buffer.
================================================================================
