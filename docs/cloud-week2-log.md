# Cloud Week 2 Log (AzureLab)

## Day 0 — 2026-11-19 (Gate: Read-Only)
- **Goal:** Verify OpenAI quota/models, decide region & policy strategy, verify AI service availability and current pricing, confirm budget/credit/`dev2` health, draft ADR-006.
- **Commands run (read-only):**
  - `az cognitiveservices usage list` & `az cognitiveservices model list` across `centralindia`, `southindia`, `eastus2`.
  - `az provider show -n Microsoft.Search` (confirmed `Central India`, `South India`, `India South Central`).
  - `az consumption budget show --budget-name budget-azurelab-week1`.
  - `Invoke-WebRequest https://app-azurelab-api-dev-cin-jyqvnq.azurewebsites.net/health/ready` → Healthy.
- **Key findings:**
  - `centralindia` has `0` pay-as-you-go OpenAI quota (only `GlobalProvisionedManaged` PTUs, no embeddings).
  - `southindia` has `GlobalStandard.gpt-5-mini` (500K TPM) and `GlobalStandard`/`DataZoneStandard` `text-embedding-3-small` (1M TPM).
  - Current docs: `gpt-4o` and `gpt-4.1` retire `2027-04-14`; `text-embedding-3-small` retires `2028-02-09`; cross-region Private Endpoints (VNet/PE in `centralindia` → OpenAI in `southindia`) are supported over the Azure backbone.
  - Current pricing verified: AI Search Basic \$73.73/mo (\$0.101/hr), Private Endpoint \$0.01/hr + \$0.50/DNS zone/mo, Service Bus Premium \$0.928/MU/hr (~\$677/mo).
  - Credit: **\$183.51 remaining**, **22 days left**, **\$16.67** actual cost.
- **Decisions:**
  - **GO** for Week 2. Selected **Option A** (OpenAI in `southindia`, all other services in `centralindia`), amend `allowed-locations-cin` on Day 1.
  - Drafted `docs/adr/006-ai-region-and-models.md` and added region-planning learning note to `docs/interview-prep/learning-notes.md`.
- **Issues / Notes:**
  - `az cognitiveservices account list-skus --kind ContentSafety|FormRecognizer` JMESPath filter returned empty; docs confirm GA in `centralindia` and `southindia` — re-verify subscription SKU directly on Day 3/4 before deploying.
  - Budget notifications in CLI showed differing email strings across thresholds; verify in Portal if not redacted.
