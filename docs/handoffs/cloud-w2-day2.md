# Cloud Week 2 — Day 2 Handoff (Azure AI Search behind `IDocumentVectorStore`)

Date: 2026-11-20 | Status: **DONE** | `Ai:Enabled` in Azure at EOD: **`false`** (`srch-azurelab-dev-cin-jyqvnq` deleted after E2E & negative tests; `what-if` drift = **0**)

> **Operational Deployment Rule (from today's lesson):** **Avoid manual Windows ZIP deployments (`Compress-Archive` / local `az webapp deploy`).** Always deploy code changes through GitHub Actions ([apps.yml](file:///d:/2026/AzureLabs/.github/workflows/apps.yml) on `ubuntu-latest`). Windows `Compress-Archive` omits dot-prefixed folders (`.azurefunctions/` -> Functions 404) and writes backslash (`\`) entry paths (`runtimes\unix\lib\...` -> Linux `WEBSITE_RUN_FROM_PACKAGE=1` fails to load `Microsoft.Data.SqlClient.dll` -> API 500).

---

## 1. Azure AI Search Resource & Day 2 Spend (`rg-azurelab-ndvd-dev2-cin`)

| Resource | Name | Region | SKU / Settings | Lifecycle Today | Spend |
|---|---|---|---|---|---|
| Azure AI Search | `srch-azurelab-dev-cin-jyqvnq` | `centralindia` | `basic` (`1 replica × 1 partition`, \$0.101/hr), `disableLocalAuth: true` (`authOptions` omitted), `semanticSearch: 'free'` (1,000 free queries/mo), `publicNetworkAccess: 'enabled'`, tag `expires: 2026-11-20` | Created **10:06 IST**, deleted **~15:45 IST** (~5.65 hrs billed = 6 SU-hours) | **~\$0.61** |
| Azure OpenAI | `oai-azurelab-dev-sin-jyqvnq` | `southindia` | `S0` (\$0/hr idle) — `gpt-5-mini` + `text-embedding-3-small` (`1536` dims) | Kept (used for re-index, 3×12 eval runs, E2E `/ask`) | **~\$0.03** |
| `dev2` Baseline | App Service Plan (`P0v3`), SQL Serverless, SB Standard, Storage, Logs | `centralindia` | `Ai__Enabled` toggled `true` for Step 7/8, restored to `false` at EOD | Kept | ~\$2.40/day baseline |

- **Day 2 incremental AI spend:** **~\$0.64** (AI Search ~\$0.61 + OpenAI tokens ~\$0.03).
- **Estimated credit remaining:** **~\$180.50 of \$200** (~21 days left).

**Bicep & code artifacts:**
- Infra: [aisearch.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/aisearch.bicep), [roleassignments.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/roleassignments.bicep), [appservice.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/appservice.bicep), [functionapp.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/functionapp.bicep), [main.bicep](file:///d:/2026/AzureLabs/infra/bicep/main.bicep), [dev.bicepparam](file:///d:/2026/AzureLabs/infra/bicep/params/dev.bicepparam).
- Provider: [SearchChunkDocument.cs](file:///d:/2026/AzureLabs/AzureLab.Rag/Providers/AzureAISearch/SearchChunkDocument.cs), [SearchIndexSchemaBuilder.cs](file:///d:/2026/AzureLabs/AzureLab.Rag/Providers/AzureAISearch/SearchIndexSchemaBuilder.cs), [AzureAiSearchDocumentVectorStore.cs](file:///d:/2026/AzureLabs/AzureLab.Rag/Providers/AzureAISearch/AzureAiSearchDocumentVectorStore.cs), [AzureAiSearchRegistration.cs](file:///d:/2026/AzureLabs/AzureLab.Rag/Providers/AzureAISearch/AzureAiSearchRegistration.cs).
- Deploy-time CLI: [AzureLab.SearchTool/Program.cs](file:///d:/2026/AzureLabs/AzureLab.SearchTool/Program.cs).

---

## 2. RBAC Roles Table (Least Privilege, Scope: `srch-azurelab-dev-cin-jyqvnq`)

| Principal | Role Name | Role Definition GUID | Plane | Justification |
|---|---|---|---|---|
| `app-azurelab-api-dev-cin-jyqvnq` (MI) | `Search Index Data Reader` | `1407120a-92aa-4202-b7e9-c0e197c71c8f` | Data plane (read) | Read-only hybrid + semantic queries on `/events/{id}/ask`; cannot mutate documents or schema |
| `func-azurelab-dev-cin-jyqvnq` (MI) | `Search Index Data Contributor` | `8ebe5a00-799e-43f5-93ac-243d3dce84a7` | Data plane (read/write) | Upserts new chunks and deletes stale ETag chunks in `DocumentIngestionFunction`; cannot mutate index schema |
| Dev User (`AZURELAB_SQL_ADMIN_OID`) *(and CI deploy UAMI in prod)* | `Search Service Contributor` + `Search Index Data Contributor` | `7ca78c08-252a-4471-8644-bb5ff32d4ba0` + `8ebe5a00-799e-43f5-93ac-243d3dce84a7` | Control plane + Data plane | Runs deploy-time `AzureLab.SearchTool` (`ensure-index`, `delete-index`, `alias`, `reindex`) |
| `app-azurelab-worker-dev-cin-jyqvnq` (MI) | *None* | — | — | Worker handles redeem/outbox only; zero AI Search access |

**Key RBAC & Alias Finding:**
- Runtime identities (`API` with `Search Index Data Reader`, `Functions` with `Search Index Data Contributor`) have **no** `Search Service Contributor` permission. When `Ai__VectorStore__AzureAISearch__IndexName` was set to the alias `azurelab-docs`, data-plane queries under `Search Index Data Reader` returned **`404 The index 'azurelab-docs' for service 'srch-azurelab-dev-cin-jyqvnq' was not found`**, whereas pointing `aiSearchIndexName = 'azurelab-docs-v2'` directly succeeded immediately without elevating runtime identities to `Search Service Contributor`.

---

## 3. Index Schema (`azurelab-docs-v1` / `azurelab-docs-v2`)

Defined in [SearchIndexSchemaBuilder.cs](file:///d:/2026/AzureLabs/AzureLab.Rag/Providers/AzureAISearch/SearchIndexSchemaBuilder.cs):

| Field | Edm Type | Key | Searchable | Filterable | Sortable | Retrievable | Purpose & Notes |
|---|---|---|---|---|---|---|---|
| `id` | `Edm.String` | **Yes** | No | No | No | Yes | `LowerHex(SHA256("{DocumentKey}\|{ChunkIndex}"))` (64 hex chars `[0-9a-f]`; avoids leading `_` rejected by Search) |
| `eventId` | `Edm.String` | No | No | **Yes** | No | Yes | Mandatory server-side OData filter: `SearchFilter.Create($"eventId eq {eventId:D}")` |
| `documentKey` | `Edm.String` | No | No | **Yes** | No | Yes | `"{blobName}\|{etag}"` — version identity & citations |
| `blobName` | `Edm.String` | No | No | **Yes** | No | Yes | Used to locate & purge older ETag chunks for `(eventId, blobName)` |
| `chunkIndex` | `Edm.Int32` | No | No | **Yes** | **Yes** | Yes | Chunk sequence & cleanup of leftover indexes on shorter re-uploads |
| `content` | `Edm.String` | No | **Yes** (`en.microsoft`) | No | No | Yes | BM25 lexical search, semantic L2 cross-encoder input, LLM grounding text |
| `contentHash` | `Edm.String` | No | No | No | No | Yes | SHA-256 content integrity / audit |
| `embedding` | `Collection(Edm.Single)` | No | **Yes** (`1536`d, `vprofile-hnsw`) | No | No | **No (`IsHidden=true`)** | HNSW (`cosine`, `m=4`, `efConstruction=400`, `efSearch=500`); hidden to save response payload |
| `embeddingModel` | `Edm.String` | No | No | **Yes** | No | Yes | Records `text-embedding-3-small` for migration audit |
| `sourceVersionUtc` | `Edm.DateTimeOffset` | No | No | **Yes** | **Yes** | Yes | Out-of-order protection (`Superseded` check before upsert) |

- **Semantic Configuration (`sem-default`):** `TitleField = "blobName"`, `ContentFields = ["content"]`.

---

## 4. Eval Comparison Table (12-Question Set: [handbook-eval.json](file:///d:/2026/AzureLabs/infra/local/eval/handbook-eval.json))

Added 2 enterprise contrast cases to the original 10-question set:
- **Q8 (Exact numeric policy token):** *"What is the exact 14-day cancellation refund rule?"* (tests BM25 + vector hybrid)
- **Q9 (Heavy paraphrase, zero keyword overlap):** *"If my flight is delayed and I drop out a week and a half beforehand, do I get anything back?"* (tests vector + semantic ranker threshold sensitivity)

| Metric | 1. `pgvector-local` (Ollama `nomic-embed-text` 768d + local LLM) | 2. `search-VectorOnly` (`text-embedding-3-small` 1536d + `gpt-5-mini`) | 3. `search-Hybrid` (BM25 + Vector RRF + `gpt-5-mini`) | 4. `search-HybridSemantic` (BM25 + Vector + L2 Semantic Ranker + `gpt-5-mini`) |
|---|---|---|---|---|
| **Overall Pass Rate** | **12 / 12** (100%) | **12 / 12** (100%) | **11 / 12** (91.7%) | **11 / 12** (91.7%) |
| **Expected Source Hit Rate** (`handbook.pdf` on 9 answerable Qs) | **9 / 9** (`1.000`) | **9 / 9** (`1.000`) | **9 / 9** (`1.000`) | **8 / 9** (`0.889` — Q9 scored `0.40`, below `MinSimilarity=0.45`) |
| **Unanswerable Filtering (`NoRelevantContext` before LLM)** | **1 / 3** (`dress code` `0.68` & `lunch` `0.66` called LLM) | **0 / 3** (all 3 called LLM: `0.66`, `0.64`, `0.50`) | **0 / 3** (RRF rank-based: `1.0`, `1.0`, `0.51` — all called LLM) | **3 / 3** (`dress code` `0.44`, `lunch` `0.35`, `France` `0.15` -> **0 LLM calls!**) |
| **Avg Embed Latency (`avgEmbedMs`)** | `1,932 ms` | `2,432 ms` | `2,674 ms` | `2,720 ms` |
| **Avg Search Latency (`avgSearchMs` / warm range)** | **`47 ms`** (warm: **4–9 ms**) | `2,016 ms` (warm: **50–89 ms**, 1 outlier) | `433 ms` (warm: **53–145 ms**) | `461 ms` (warm: **62–129 ms**) |
| **Avg Generate Latency (`avgGenerateMs`)** | `10,756 ms` | `3,508 ms` | `3,771 ms` | **`1,903 ms`** (~46% faster than VectorOnly, ~5.6× faster than Ollama) |
| **Total Input Tokens (12 Qs)** | `4,061` | `5,002` | `5,002` | **`2,340`** (**-53.2%**) |
| **Total Output Tokens (12 Qs)** | `258` | `3,200` *(incl. reasoning tokens)* | `3,480` *(incl. reasoning tokens)* | **`1,493`** (**-53.3%**) |
| **Est. Token Cost (12 Qs / per Q)** | \$0 (local CPU) | ~\$0.0077 (~\$0.00064/Q) | ~\$0.0082 (~\$0.00068/Q) | **~\$0.0036 (~\$0.00030/Q)** |

### Evidence-Backed Conclusions
1. **Why `HybridSemantic` cut LLM tokens & cost by >53%:** In `VectorOnly` and `Hybrid`, on-topic absent questions (`"dress code"`, `"lunch"`) share event vocabulary with `handbook.pdf`, scoring `0.64–0.66` (vector) or `1.0` (RRF) and forcing an expensive `gpt-5-mini` call just to say `"I don't know"`. The Semantic L2 cross-encoder jointly attends to query + passage and pushed all 3 unanswerable questions below `0.45` (`0.44`, `0.35`, `0.15`), short-circuiting to `NoRelevantContext` with **0 LLM tokens**.
2. **Why you must never threshold on raw RRF (`Hybrid`):** Reciprocal Rank Fusion scores depend only on **rank position** (`1/(60+rank)`), not absolute relevance — even `"What is the capital of France?"` ranked #1 in one leg (`0.51` normalized).
3. **Threshold calibration trade-off (Q9):** The heavy paraphrase Q9 (`"drop out a week and a half beforehand"`) received a semantic reranker score of `1.60 / 4.0` (`0.40` normalized), just under `MinSimilarity = 0.45`. Lowering `MinSimilarity` for `HybridSemantic` to `0.38` recovers Q9 (9/9 recall) while still blocking `"lunch"` (`0.35`) and `"capital of France"` (`0.15`).
4. **Why `gpt-5-mini` output tokens (`3,200`) are ~12× Ollama (`258`):** `gpt-5-mini` is a reasoning model whose `OutputTokenCount` includes hidden `reasoning_tokens` (~200–250 tokens/call).

---

## 5. Re-Index & Blue/Green Migration Runbook (`AzureLab.SearchTool`)

### A. Rebuild Index in Place (or after Disaster / Deletion)
```powershell
$tool = "AzureLab.SearchTool/AzureLab.SearchTool.csproj"
$env:Ai__Enabled = "true"
$env:Ai__Provider = "AzureOpenAI"
$env:Ai__AzureOpenAI__Endpoint = "https://oai-azurelab-dev-sin-jyqvnq.openai.azure.com/"
$env:Ai__Chat__Model = "gpt-5-mini"
$env:Ai__Chat__TimeoutSeconds = "60"
$env:Ai__Embedding__Model = "text-embedding-3-small"
$env:Ai__Embedding__Dimensions = "1536"
$env:Ai__Embedding__TimeoutSeconds = "30"
$env:Ai__Embedding__DocumentPrefix = ""
$env:Ai__Embedding__QueryPrefix = ""
$env:Ai__VectorStore__Provider = "AzureAISearch"
$env:Ai__VectorStore__AzureAISearch__Endpoint = "https://srch-azurelab-dev-cin-jyqvnq.search.windows.net"
$env:Ai__VectorStore__AzureAISearch__IndexName = "azurelab-docs-v2"
$env:ConnectionStrings__LabDb = "<SQL connection string>"
$env:EventDocsStorage__blobServiceUri = "https://stazurelabdevjyqvnq.blob.core.windows.net/"

# Idempotent: ensures schema, queries latest Accepted EventDocuments per (EventId, BlobName), streams blobs with IfMatch ETag, upserts chunks, deletes stale chunks
dotnet run --project $tool -- reindex --index azurelab-docs-v2
```

### B. Embedding Model / Dimension Change (Blue/Green Versioned Index)
1. **Why in-place update fails (verified live):** Attempting to change `embedding` dimensions (`768 -> 1536`) on `azurelab-docs-768` fails with HTTP **`400 Bad Request` (`ErrorCode: OperationNotAllowed`, `CannotChangeExistingField: "Existing field 'embedding' cannot be changed."`)**.
2. **Zero-downtime procedure:**
   - Set `Ai__Embedding__Model` and `Ai__Embedding__Dimensions` for the new model in the CLI shell.
   - Build and populate the new versioned index side-by-side:
     `dotnet run --project $tool -- reindex --index azurelab-docs-v2`
   - Update alias (`dotnet run --project $tool -- alias azurelab-docs azurelab-docs-v2`) or flip `aiSearchIndexName = 'azurelab-docs-v2'` via Bicep.
   - Delete the old unaliased index:
     `dotnet run --project $tool -- delete-index --index azurelab-docs-v1`
   - *(Note: Azure AI Search blocks deleting an index while an alias still maps to it with `400 OperationNotAllowed`.)*

---

## 6. Lab Evidence Summary (E2E & Negative Tests)
- **Leading underscore key trap caught & fixed:** Base64Url keys hit a leading `_` ~1/64 times, failing with `400 InvalidDocumentKey: "Keys cannot start with a leading underscore."` Switched `SearchChunkDocument.ComputeKey` to 64-char lowercase hex (`SHA256`).
- **Azure E2E (`Ai:Enabled=true`, deployed via `apps.yml`):** Uploaded `e78db890-e326-4461-ace6-a48f272c654f/handbook.pdf` -> `EventDocumentFunction` -> Service Bus `document-processed` -> `DocumentIngestionFunction` -> `/events/e78db890-e326-4461-ace6-a48f272c654f/ask` returned:
  - Unenrolled user test -> **`403 Forbidden`** (`EfEventAccessService` security trimming).
  - Enrolled user test -> **`200 OK`**, `answered: true`, `"You must cancel at least 14 calendar days before the event to receive a full refund [1]."`, `similarity: 0.794`, `citedInAnswer: true`.
- **Step 8 Role Negative Test:** Removed `Search Index Data Reader` from `app-azurelab-api-dev-cin-jyqvnq`. Unlike Azure OpenAI (>5 min cache on Day 1), Azure AI Search enforced the revocation **at `t+0s`** (`Azure.RequestFailedException: Status: 403 (Forbidden)` in API logs, HTTP `500` returned to caller). Restored via Bicep, then tore down Search and set `Ai:Enabled=false` (`what-if` = 0 changes).

---

## 7. What Day 3 Needs
1. **Azure AI Content Safety (`Prompt Shields` + text moderation)** via Bicep in `centralindia` (`S0`, `disableLocalAuth: true`, Managed Identity role `Cognitive Services User`).
2. Re-enable AI Search briefly from Bicep (`deploySearch = true`, `aiEnabled = true`) + re-index `handbook.pdf` via `AzureLab.SearchTool`, run the **Prompt Injection & Tool-Calling security evals** against `gpt-5-mini` + Content Safety gate, and verify **GenAI OpenTelemetry metrics/traces + cost guardrails**.
3. Deploy all app changes exclusively through GitHub Actions (`apps.yml`), never manual Windows zip deploy.
