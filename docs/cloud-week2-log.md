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


## Day 1 — 2026-11-19 (Azure OpenAI via Bicep, Identity-Only)
- **Goal:** Provision Azure OpenAI in `southindia` with `disableLocalAuth=true`, implement `Ai:Provider=AzureOpenAI` via `Microsoft.Extensions.AI` + `DefaultAzureCredential`, verify local + deployed MI, run 429/quota lab and negative auth/RBAC tests, document model lifecycle.
- **Decisions:**
  - Chose `kind: 'OpenAI'` (`oai-azurelab-dev-sin-jyqvnq`, single Private DNS zone on Day 5) over `AIServices`; `GlobalStandard` for `gpt-5-mini` (`2025-08-07`, cap `10`) and `text-embedding-3-small` (`1`, cap `10`), both pinned with `NoAutoUpgrade`.
  - Isolated `gpt-5-mini`'s `temperature` restriction inside `ReasoningModelChatClient` middleware (`AzureOpenAiRegistration.cs`) so business code stays provider-agnostic.
  - Used Option 2 (`az webapp ssh` -> `$IDENTITY_ENDPOINT`) for today's deployed MI check to avoid throwaway `VectorStore=None` scaffolding before Day 2's AI Search deploy.
  - Retry ownership: Azure SDK (`System.ClientModel`) owns transient `429`/`5xx` retries (no stacked outer Polly retry); interactive API fails fast if `Retry-After` breaches SLA; Functions rely on Service Bus retry -> DLQ.
  - Budget email notifications confirmed present in Portal (redacted in CLI output).
- **Commands / Key Evidence:**
  - `az policy assignment list` → empty (policy `allowed-locations-cin` did not exist; deferred to Day 6 Policy demo with `["centralindia","southindia"]`).
  - `az deployment group what-if` & `create` → created `oai-azurelab-dev-sin-jyqvnq`, 2 deployments, 3 `Cognitive Services OpenAI User` role assignments (API MI, Functions MI, dev user).
  - Local `/dev/ai/ping` → `embeddingDimensions: 1536`, `chat: pong`; `/dev/rag/selftest` → `Npgsql.PostgresException 22000: expected 768 dimensions, not 1536`.
  - Deployed API SSH -> `$IDENTITY_ENDPOINT` -> OpenAI → both deployments succeeded (`gpt-5-mini` spent `64` hidden `reasoning_tokens` out of `74` completion tokens for `"pong"`).
  - 429 lab (`capacity: 1`, 10 concurrent calls) → Calls 1–2 `200`, Calls 3–10 `429` (`Retry-After: 30`, `code: "rate_limit_exceeded"`, RPM hit before TPM). Restored `capacity: 10` via Bicep.
  - Negative tests: Key auth → `403 AuthenticationTypeDisabled`; RBAC removal → still `200` at `t+299s` (gateway RBAC cache 5–10 min), restored via Bicep.
- **Mistakes & Gaps Identified (Learn-From-This):**
  1. **Assumed `allowed-locations-cin` policy existed from Week 1 docs** without verifying live — always query live state (`az policy assignment list`) before planning policy edits.
  2. **Ran `what-if` in a fresh PowerShell session without Week 1 env vars loaded**, causing `sql.bicep` `adminObjectId` validation failure (`''`). Keep a session-init script/snippet for `AZURELAB_*` env vars.
  3. **Expected `401` on disabled key auth** — Azure OpenAI actually returns **`403 AuthenticationTypeDisabled`** (policy forbids the auth method, not invalid credentials).
  4. **Thought RBAC revocation takes effect when the Entra JWT expires** — wrong; Azure RBAC roles are **not** inside the JWT. The resource data-plane gateway caches RBAC decisions for **5–10 minutes**, so a 5-minute poll loop (`t+299s`) was too short to catch the `403`. Allow up to **10 minutes** in Days 2–4 negative RBAC labs.
  5. **Initial sequential 6-call burst at `capacity: 1` all returned `200` (`rem-tok=9984`)** because control-plane -> data-plane quota changes have propagation lag and sequential calls let the sliding window refill. Concurrent calls after propagation proved **RPM exhaustion** (`rem-tok=975` still available).
  6. **Windows PowerShell 5.1 `[System.Net.Http.HttpClient]`** requires `Add-Type -AssemblyName System.Net.Http` before use.
  7. **Reasoning model (`gpt-5-mini`) token & parameter traps:** rejects custom `temperature` (`400`) and consumes hidden `reasoning_tokens` out of `max_completion_tokens` (64 tokens just for `"pong"`), requiring higher `MaxAnswerTokens` (`800–1000`) than non-reasoning models.
