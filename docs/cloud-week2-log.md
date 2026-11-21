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


## Day 2 — 2026-11-20 (Azure AI Search behind `IDocumentVectorStore`, Eval & Migration Drill)
- **Goal:** Create `aisearch.bicep` (Basic, `disableLocalAuth: true`, `semanticSearch: 'free'`), enforce least-privilege RBAC (API `Search Index Data Reader`, Functions `Search Index Data Contributor`, deploy-time CLI `Search Service Contributor`), implement `AzureAISearch` provider behind `IDocumentVectorStore` + `AzureLab.SearchTool` re-indexer, run 12-question eval (`pgvector` vs `VectorOnly` vs `Hybrid` vs `HybridSemantic`), run dimension-change + versioned index drill, verify full Azure E2E (`Ai:Enabled=true`), negative-test Search RBAC, and delete Search at EOD.
- **Decisions:**
  - **Index creation ownership:** Kept runtime identities (`API`, `Functions`) strictly data-plane; created `AzureLab.SearchTool` for deploy-time schema creation (`ensure-index`), `delete-index`, `alias`, and idempotent `reindex` from `EventDocuments` + `event-docs` blobs.
  - **Deterministic key:** Switched `SearchChunkDocument.ComputeKey` from Base64Url to 64-char lowercase hex (`Convert.ToHexString(SHA256)`), because Base64Url produces a leading `_` ~1 in 64 hashes which Azure AI Search rejects (`InvalidDocumentKey: Keys cannot start with a leading underscore`).
  - **Upsert-before-delete replacement:** Since AI Search lacks multi-document ACID transactions, `ReplaceDocumentAsync` checks `sourceVersionUtc` for `Superseded`, upserts new chunks first (zero read-gap), then deletes older-ETag and leftover-index chunk IDs.
  - **Score normalization across modes:** `VectorOnly` uses raw `[0,1]` score; `Hybrid` normalizes 2-leg RRF by `2/61`; `HybridSemantic` normalizes `RerankerScore / 4.0` (`[0,4] -> [0,1]`).
  - **Direct versioned index name for least-privilege runtime apps:** Pointed `aiSearchIndexName = 'azurelab-docs-v2'` in Bicep because runtime identities with only `Search Index Data Reader` / `Contributor` (no `Search Service Contributor`) received `404 Index not found` when resolving the alias name.
  - **CI-only app deployments going forward:** Manual Windows zip deployments caused two avoidable outages today; always deploy via GitHub Actions `apps.yml`.
- **Commands / Key Evidence:**
  - Provisioned `srch-azurelab-dev-cin-jyqvnq` at **10:06 IST**; deleted at **~15:45 IST** (~6 SU-hours ≈ **\$0.61**).
  - `AzureLab.SearchTool reindex` Run 1 & Run 2 both produced `stored=1 (2 chunks), failed=0` (idempotent).
  - Attempting to delete `azurelab-docs-v1` while aliased by `azurelab-docs` -> `400 OperationNotAllowed` (alias delete guard).
  - Attempting to change `embedding` dimensions (`768 -> 1536`) on `azurelab-docs-768` -> `400 OperationNotAllowed` (`CannotChangeExistingField: "Existing field 'embedding' cannot be changed."`). Solved via side-by-side `azurelab-docs-v2` + alias flip + deleting `-v1`.
  - **12-question eval (`handbook-eval.json`):**
    - `pgvector-local`: 12/12 pass, `sourceHitRate=1.0`, `avgSearchMs=47` (4–9 ms warm), `avgGenerateMs=10,756`, `4,061 in / 258 out` tokens.
    - `search-VectorOnly`: 12/12 pass, `sourceHitRate=1.0`, `avgSearchMs=2,016` (50–89 ms warm), `avgGenerateMs=3,508`, `5,002 in / 3,200 out` tokens (all 3 unanswerable Qs called LLM).
    - `search-Hybrid`: 11/12 pass, `sourceHitRate=1.0`, `avgSearchMs=433` (53–145 ms warm), `avgGenerateMs=3,771`, `5,002 in / 3,480 out` tokens (RRF scores are rank-based so all 3 unanswerable Qs scored `0.51–1.0` and called LLM).
    - `search-HybridSemantic`: 11/12 pass, `sourceHitRate=0.889` (paraphrase Q9 scored `0.40` vs `0.45` threshold), `avgSearchMs=461` (62–129 ms warm), **`avgGenerateMs=1,903`**, **`2,340 in / 1,493 out` tokens (-53% token cost!)** because the semantic L2 reranker dropped all 3 unanswerable Qs (`0.44`, `0.35`, `0.15`) to `NoRelevantContext` before calling OpenAI.
  - **Azure E2E (`Ai:Enabled=true`):** Unenrolled user -> `403 Forbidden`; enrolled user on `e78db890-e326-4461-ace6-a48f272c654f` -> `200 OK`, grounded 14-day refund answer + citation (`similarity: 0.794`).
  - **Negative RBAC test:** Deleted `Search Index Data Reader` from API MI -> failed at **`t+0s`** (`Azure.RequestFailedException: Status: 403 (Forbidden)`). Restored via Bicep, then deleted Search & set `Ai:Enabled=false` (`what-if` = 0 drift).
- **Mistakes & Gaps Identified (Learn-From-This):**
  1. **Manual Windows ZIP deployments (`Compress-Archive` / `tar -C .` / `[ZipFile]::CreateFromDirectory`):**
     - `Compress-Archive -Path ./publish/func/*` skipped dot-prefixed `.azurefunctions/` -> Functions returned `404`.
     - `tar -a -c -f ... -C ./publish/func .` prefixed entries with `./` -> Functions returned `503`.
     - `Compress-Archive` on `api.zip` wrote Windows backslashes (`runtimes\unix\lib\...`), causing Linux `WEBSITE_RUN_FROM_PACKAGE=1` to fail with `FileNotFoundException: Microsoft.Data.SqlClient`.
     - **Fix:** Never do manual Windows zip deploys when a working Linux CI pipeline (`apps.yml`) exists.
  2. **Base64Url document keys in Azure AI Search:** Base64Url includes `_` and `-`, and Azure AI Search forbids document keys starting with a leading underscore (`400 InvalidDocumentKey` on ~1/64 chunks). Use lowercase hex (`Convert.ToHexString(SHA256)`) or prefix keys with a letter.
  3. **`Azure.Search.Documents` `11.7.0` SDK vs Index Aliases:** `SearchAlias` / `CreateOrUpdateAliasAsync` is only in preview SDKs, not GA `11.7.0`. Used the GA REST API (`PUT /aliases('{name}')?api-version=2026-04-01`) with a `DefaultAzureCredential` Bearer token (`https://search.azure.com/.default`).
  4. **Index Alias resolution under least-privilege `Search Index Data Reader`:** Querying an alias name (`azurelab-docs`) worked locally under Dev User (`Search Service Contributor` + `Index Data Contributor`) but returned `404 Index not found` under API MI (`Search Index Data Reader` only). Pointing `Ai__VectorStore__AzureAISearch__IndexName` to the concrete versioned index (`azurelab-docs-v2`) preserved strict least privilege.
  5. **Thresholding on RRF scores (`Hybrid` mode):** RRF scores depend only on rank (`1/(60+rank)`), so even completely off-topic queries (`"What is the capital of France?"`) get rank #1 in one leg (`0.51` normalized). Only threshold on cosine similarity (`VectorOnly`) or Semantic L2 `RerankerScore` (`HybridSemantic`).
  6. **AI Search vs OpenAI RBAC revocation latency:** While Azure OpenAI cached removed RBAC roles for >5 minutes on Day 1, Azure AI Search enforced `Search Index Data Reader` removal at **`t+0s`** (`403 Forbidden`).


## Day 3 — 2026-11-21 (Content Safety Gate, Day 6 Security & Tool-Calling Evals on `gpt-5-mini`, GenAI Telemetry, Cost Guardrails)
- **Decisions:**
  - Verified Prompt Shields regional availability: unsupported in `centralindia`, supported in **`southindia`** — co-located `cs-azurelab-dev-sin-jyqvnq` (`S0`, `disableLocalAuth: true`) with Azure OpenAI in `southindia` (already allowed by `allowed-locations-cin`).
  - Added `IContentSafetyGate` (`AzureLab.Rag`) + `IAssistantSafetyGate` (`AzureLab.Assistant`, keeping Clean Architecture dependency-free) using REST `2024-09-01` (`text:shieldPrompt`, `text:analyze`) + `DefaultAzureCredential` (0 new NuGet packages).
  - Fail modes: **fail-closed** on user input & retrieved chunks (with surgical chunk dropping); **fail-open** on model output (AOAI built-in output filter provides defense-in-depth). Raised `TimeoutSeconds` from `3s` to `10s` after cold-start token/TLS latency hit 3s.
  - Enabled OTel GenAI semantic conventions via `Microsoft.Extensions.AI` `.UseOpenTelemetry(sourceName: RagTelemetry.Name)` (`EnableSensitiveData = false`), added `rag.cost.usd` metric/span tag, and deployed `alert-openai-http-429` in `alerts.bicep`.
  - Kept `srch-azurelab-dev-cin-jyqvnq` running per user request because Day 4 runs today (delete at Day 4 EOD).
- **Commands & Evidence:**
  - Recreated AI Search + deployed Content Safety & `alert-openai-http-429` via `az deployment group create`; re-indexed `azurelab-docs-v2` via `AzureLab.SearchTool`.
  - Prompt Shields keyless smoke test: `userPromptAnalysis.attackDetected: false`, `documentsAnalysis[0].attackDetected: true`.
  - Security lab + live `gpt-5-mini` Gate OFF vs Gate ON: direct jailbreak switched from `BlockedByModelFilter` (paid embed+search first) to `BlockedBySafety` (Gate 1, 0 tokens); poisoned doc cleanly answered `"Registration opens at 9:00 AM at the main hall. [1]"` via surgical chunk drop.
  - 15-case tool-calling eval: `gpt-5-mini` **14/15 (93%)**, avg `4,895 ms` (~2.8s warm), `553` in / `132` out vs `qwen2.5:3b` `9–11/15` (`13–16s`) and `llama3.2:3b` `11–12/15` (`15s`). Only `gpt-5-mini` failure was raw `R2` (`ProposeRedeem` called on jailbreak) — proving Gate 1 + deterministic code controls are essential even on frontier models.
  - Live OTel span verified: `chat gpt-5-mini` emitted `gen_ai.usage.input_tokens=186`, `gen_ai.usage.output_tokens=148`, `gen_ai.usage.reasoning.output_tokens=128`, `rag.estimated_cost_usd=0.000342`, zero PII.
- **Issues & Fixes:**
  - Local `/ask` test returned `401` with `X-Dev-User` because `Program.cs` uses real Entra JWT Bearer since Week 1 -> switched to `az account get-access-token --resource api://$AZURELAB_API_CLIENT_ID` + `EventEnrollments` row.
  - `ContentSafetyOptions.TimeoutSeconds = 3` timed out on cold token acquisition -> raised to `10s` in Bicep (`30s` locally).
  - `AssistantEvalEndpoint` hardcoded `OllamaApiClient` and aborted on HTTP 400 `content_filter` -> updated to use DI `IChatClient` for `AzureOpenAI`, raised `MaxOutputTokens` for reasoning tokens, and scored `content_filter` cleanly.
