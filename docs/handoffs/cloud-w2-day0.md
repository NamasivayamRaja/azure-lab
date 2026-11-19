# Cloud Week 2 — Day 0 Handoff (Gate: GO)

Date: 2026-11-19 | Mode: Read-only (zero Azure changes made) | Gate Result: **GO**

## 1. Current Azure State (Verified Healthy)
- **Subscription:** `sub-azurelab-sandbox` (Free Trial — **\$183.51 remaining**, **22 days left**, **\$16.67** actual billed to date)
- **Workload RG (kept baseline):** `rg-azurelab-ndvd-dev2-cin` (`centralindia`, suffix `jyqvnq`)
  - `asp-azurelab-dev-cin` (Linux P0v3, autoscale 1..2)
  - `app-azurelab-api-dev-cin-jyqvnq` (`/health/ready` verified **Healthy** on Day 0) + staging slot (stopped)
  - `app-azurelab-worker-dev-cin-jyqvnq` (slotless)
  - `func-azurelab-dev-cin-jyqvnq` + staging slot (stopped)
  - `stazurelabdevjyqvnq` (Standard_LRS, `allowSharedKeyAccess=false`)
  - `sb-azurelab-dev-cin-jyqvnq` (Standard, `disableLocalAuth=true`)
  - `sql-azurelab-dev-cin-jyqvnq` / `sqldb-azurelab-dev-cin` (`GP_S_Gen5_1`, Entra-only)
  - `log-azurelab-dev-cin` + `appi-azurelab-dev-cin` + alerts + autoscale
- **Shared RG:** `rg-azurelab-ndvd-shared-cin` (`id-azurelab-github-dev-cin` UAMI + federated creds)
- **Policy:** `allowed-locations-cin` (currently restricts to `centralindia`)
- **Budget:** `budget-azurelab-week1` (₹3,000/mo; 50/80/100% actual + 100% forecast) — *verify in Portal that all 4 notification thresholds point to your intended email address if they differed in CLI output.*

## 2. Day 0 Decisions
- **Gate Result:** **GO** (full Week 2 plan runs as scheduled; no upgrade or compression needed).
- **Region & Model Choice (Option A — drafted in [006-ai-region-and-models.md](file:///d:/2026/AzureLabs/docs/adr/006-ai-region-and-models.md)):**
  - **Azure OpenAI (`southindia`):** `gpt-5-mini` (`2025-08-07`, `GlobalStandard`, 500K TPM) + `text-embedding-3-small` (`1`, `DataZoneStandard` or `GlobalStandard`, 1M TPM; retires `2028-02-09`).
  - **AI Search Basic, Content Safety, Document Intelligence (`centralindia`):** Co-located with `dev2`.
  - **Policy Change (pending Day 1 approval):** Amend `allowed-locations-cin` to allow `centralindia` and `southindia`.
  - **Cross-Region PE (Day 5):** Verified supported — VNet + PE in `centralindia` targeting OpenAI in `southindia` over Azure backbone (`privatelink.openai.azure.com`).
- **Learning Note Added:** [learning-notes.md](file:///d:/2026/AzureLabs/docs/interview-prep/learning-notes.md) — pick primary region from the full planned workload (most constrained service), not Phase 1 alone.

## 3. Evidence Tables

### OpenAI Pay-As-You-Go Quota (`az cognitiveservices usage list`, units = 1K TPM)
| Region | Chat Quota | Embedding Quota | Decision |
|---|---|---|---|
| `centralindia` | **0** (only `GlobalProvisionedManaged` PTU) | **0** | Not viable for pay-per-token OpenAI |
| `southindia` | `GlobalStandard.gpt-5-mini` = **500K TPM** | `GlobalStandard`/`DataZoneStandard` `text-embedding-3-small` = **1M TPM**; `Standard` `3-large` / `ada-002` = 350K | **Selected (Option A)** |
| `eastus2` | `GlobalStandard.gpt-5-mini` = 500K; `Standard` `gpt-4o` = 50K / `gpt-4o-mini` = 200K (retire `2027-04-14`) | `text-embedding-3-small` = 1M (Global/DataZone) / 350K (Standard) | Rejected (cross-continent latency) |

### Regional Availability & Verified Pricing
| Service | Region Availability | Verified Current Price | Lab Rule |
|---|---|---|---|
| **AI Search Basic** (1 SU, 15 GB) | `centralindia` (CLI + docs; Semantic Ranker GA) | **\$73.73/mo (\$0.101/hr)** | Create late, delete same day (~ \$0.40/4h) |
| **Content Safety (Prompt Shields)** | `centralindia` & `southindia` (docs GA; verify CLI SKU on Day 3) | Pay-per-call (`F0`/`S0`, \$0 idle) | `S0` required for Private Endpoints |
| **Document Intelligence (Read/Layout)** | `centralindia` & `southindia` (docs GA; verify CLI SKU on Day 4) | Pay-per-call (`F0`/`S0`, \$0 idle) | `S0` required for >2 pages & Private Endpoints |
| **Private Endpoint + Private DNS** | `centralindia` VNet (cross-region target supported) | **\$0.01/hr** per PE + **\$0.50/zone/mo** | 6 PEs + 6 zones ≈ **\$1.55/day** (delete after Day 6) |
| **Service Bus Premium** | `centralindia` | **\$0.928/MU/hr (~\$677/mo)** | **Dropped as live lab** (design notes only) |

## 4. Running Resources, Cost & Credit
- **Still running:** `rg-azurelab-ndvd-dev2-cin` baseline only (P0v3 + SB Standard + SQL Serverless ≈ **~\$0.10/hr / ~\$2.40/day**). Zero new AI/network resources created on Day 0.
- **Credit remaining:** **\$183.51 of \$200** (**22 days left**). Projected Week 2 total: **\$25–\$35**.

## 5. Pending Changes & What Day 1 Needs
1. Confirm/unify the email addresses on `budget-azurelab-week1` if the different addresses shown in CLI were not redactions.
2. Amend `allowed-locations-cin` to allow `['centralindia', 'southindia']` (with user OK) before deploying `openai.bicep` in `southindia`.
3. Create `infra/bicep/modules/openai.bicep` (`disableLocalAuth: true`, `gpt-5-mini` `GlobalStandard` + `text-embedding-3-small` `DataZoneStandard`/`GlobalStandard`) and assign `Cognitive Services OpenAI User` in `roleassignments.bicep`.
4. Wire `Ai:Provider=AzureOpenAI` behind existing `IChatClient` / `ITextEmbedder` seams (keep `Ai:Enabled=false` in Azure until Day 2 adds AI Search; verify locally + one temporary deployed check).
