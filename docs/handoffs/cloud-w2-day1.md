# Cloud Week 2 — Day 1 Handoff (Azure OpenAI, Identity-Only)

Date: 2026-11-19 | Status: **DONE** | `Ai:Enabled` in Azure: **`false`** (until Day 2 adds AI Search)

## 1. Deployed Azure OpenAI Resources (`rg-azurelab-ndvd-dev2-cin`)
| Resource | Name | Region | Kind / SKU | Key Settings |
|---|---|---|---|---|
| OpenAI Account | `oai-azurelab-dev-sin-jyqvnq` | `southindia` | `OpenAI` / `S0` | `disableLocalAuth: true`, `customSubDomainName: oai-azurelab-dev-sin-jyqvnq`, `publicNetworkAccess: Enabled` (until Day 5) |
| Endpoint | `https://oai-azurelab-dev-sin-jyqvnq.openai.azure.com/` | `southindia` | — | Entra ID Bearer token only (`https://cognitiveservices.azure.com`) |
| Chat Deployment | `gpt-5-mini` | `southindia` | `GlobalStandard` / cap `10` (10K TPM) | Model `gpt-5-mini` v`2025-08-07`, `versionUpgradeOption: NoAutoUpgrade` |
| Embedding Deployment | `text-embedding-3-small` | `southindia` | `GlobalStandard` / cap `10` (10K TPM) | Model `text-embedding-3-small` v`1`, `versionUpgradeOption: NoAutoUpgrade` |

**Bicep modules:** [openai.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/openai.bicep), [roleassignments.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/roleassignments.bicep), [main.bicep](file:///d:/2026/AzureLabs/infra/bicep/main.bicep), [dev.bicepparam](file:///d:/2026/AzureLabs/infra/bicep/params/dev.bicepparam).

## 2. Role Assignments (`Cognitive Services OpenAI User` — `5e0bd9bd-7b93-4f28-af87-19fc36ad61bd`)
Scope: `oai-azurelab-dev-sin-jyqvnq` (least privilege; `Worker` has **no** OpenAI role).

| Principal | Type | PrincipalId | Justification |
|---|---|---|---|
| `app-azurelab-api-dev-cin-jyqvnq` (MI) | `ServicePrincipal` | `31eb3516-b524-4b5c-b435-7bc072960ffc` | Calls chat + embeddings at query time |
| `func-azurelab-dev-cin-jyqvnq` (MI) | `ServicePrincipal` | `474443e6-abc3-4200-8302-5861683b2b05` | Calls embeddings during document ingestion |
| Dev User (`AZURELAB_SQL_ADMIN_OID`) | `User` | `7dc8f76e-242b-4e79-ba58-d2da6bac2cbb` | Keyless local `DefaultAzureCredential` dev/verification |

## 3. Code & Config Keys (`Ai:Provider=AzureOpenAI`)
Implemented in [AzureOpenAiRegistration.cs](file:///d:/2026/AzureLabs/AzureLab.Rag/Providers/AzureOpenAI/AzureOpenAiRegistration.cs) behind `IChatClient` / `IEmbeddingGenerator` / `ITextEmbedder` using `Azure.AI.OpenAI` `2.1.0`, `Azure.Identity` `1.21.0`, `Microsoft.Extensions.AI.OpenAI` `10.11.0`. Includes `ReasoningModelChatClient` middleware to strip `Temperature` for `gpt-5*` models without provider branches in business code.

| `appsettings` Key | Azure App Setting (`__`) | Dev Value | Notes |
|---|---|---|---|
| `Ai:Enabled` | `Ai__Enabled` | `false` *(Azure today)* / `true` *(local)* | Stays `false` in Azure until Day 2 adds AI Search |
| `Ai:Provider` | `Ai__Provider` | `AzureOpenAI` | Provider switch by config only |
| `Ai:AzureOpenAI:Endpoint` *(NEW)* | `Ai__AzureOpenAI__Endpoint` | `https://oai-azurelab-dev-sin-jyqvnq.openai.azure.com/` | Enforced absolute HTTPS URI in `AiOptionsValidator` |
| `Ai:Chat:Model` | `Ai__Chat__Model` | `gpt-5-mini` | Azure deployment name |
| `Ai:Embedding:Model` | `Ai__Embedding__Model` | `text-embedding-3-small` | Azure deployment name |
| `Ai:Embedding:Dimensions` | `Ai__Embedding__Dimensions` | `1536` | Was `768` for Ollama `nomic-embed-text` |
| `Ai:Embedding:DocumentPrefix` | `Ai__Embedding__DocumentPrefix` | `""` | Validator enforces empty for `AzureOpenAI` |
| `Ai:Embedding:QueryPrefix` | `Ai__Embedding__QueryPrefix` | `""` | Validator enforces empty for `AzureOpenAI` |

## 4. Verification & Lab Evidence
- **Local (`/dev/ai/ping`, user Entra identity):** `embeddingDimensions: 1536`, `chat: pong`.
- **Local pgvector dimension mismatch (`/dev/rag/selftest`):** `Npgsql.PostgresException (0x80004005): 22000: expected 768 dimensions, not 1536` (proves schema + vector-space coupling; fix is a new index/table + full re-embed on Day 2).
- **Deployed API Managed Identity check (`az webapp ssh` -> `$IDENTITY_ENDPOINT` -> `oai-azurelab-dev-sin-jyqvnq`):** Both `text-embedding-3-small` and `gpt-5-mini` succeeded via Bearer token. **Key finding:** even `"pong"` used `completion_tokens: 74` (**`64` `reasoning_tokens`** + 10 visible).
- **429 & Quota Lab (`capacity: 1` = 1K TPM, 10 concurrent calls):**
  - Calls 1–2: `200 OK` (`x-ratelimit-remaining-tokens: 950 / 975`).
  - Calls 3–10: **`429`**, **`Retry-After: 30`**, `code: "rate_limit_exceeded"`, message: *"Your requests to gpt-5-mini for gpt-5-mini in southindia have exceeded request rate limit."* — **RPM exhaustion** occurred before TPM was depleted.
  - Also observed control-plane -> data-plane quota propagation delay immediately after lowering capacity (`rem-tok=9984` initially).
  - Restored `capacity: 10` via Bicep (`Succeeded`).
  - **Retry & Token Budget Decisions:** Let `Azure.AI.OpenAI` (`System.ClientModel`) own transient `429`/`5xx` retries (never stack outer Polly retries on top); interactive API fails fast (`503` + `Retry-After`) if wait exceeds SLA; async Functions rely on Service Bus retry -> DLQ. Budget: ~1,400 input tokens max + **`MaxAnswerTokens: 800–1000`** for reasoning models (to avoid `FinishReason=Length` from hidden `reasoning_tokens`).
- **Negative Tests:**
  - **Key auth (`api-key` header):** **`403`** (`code: "AuthenticationTypeDisabled"`, `"Key based authentication is disabled for this resource."`).
  - **RBAC removal (dev user):** Deleted `Cognitive Services OpenAI User`; fresh Bearer tokens still returned `200` at **`t+299s` (~5 min)** due to Azure OpenAI data-plane RBAC caching (can take 5–10 min). Restored via Bicep.
  - **Propagation rule for Days 2–4:** In upcoming negative RBAC tests (AI Search, Content Safety, Document Intelligence), expect **5–10 min** data-plane RBAC cache propagation before a `403` appears — poll up to 10 min (don't stop at 5 min) and restore via Bicep.

## 5. Model Lifecycle & Retirement (Doc-Verified)
| Deployment | Model / Version | Pinning | Retirement (Public Cloud) | Blast Radius |
|---|---|---|---|---|
| `gpt-5-mini` | `gpt-5-mini` / `2025-08-07` | `NoAutoUpgrade` | **2027-07-13** | Low — deploy successor, run evals, swap `Ai__Chat__Model` |
| `text-embedding-3-small` | `text-embedding-3-small` / `1` | `NoAutoUpgrade` | **2028-02-09** | **High** — old vectors unusable; must create new index, re-embed full corpus from blobs, blue/green swap |

## 6. Running Resources, Cost & Drift Notes
- **Running:** `dev2` baseline (~\$0.10/hr / ~\$2.40/day) + `oai-azurelab-dev-sin-jyqvnq` (**\$0/hr idle**, pay-per-token only; Day 1 token spend < \$0.05).
- **Credit:** ~\$181–\$183 remaining of \$200 (~22 days left; check Cost Management at Day 2 start).
- **Doc drift found:** `allowed-locations-cin` Azure Policy does **not** exist on the subscription (`az policy assignment list` returned empty). Will create India-only `["centralindia","southindia"]` policy during Day 6's Azure Policy demo.

## 7. What Day 2 Needs
1. Create `infra/bicep/modules/aisearch.bicep` (Azure AI Search **Basic** in `centralindia`, `disableLocalAuth: true`, semantic ranker enabled; tag `expires` and delete same day after lab to save \$0.101/hr).
2. Add AI Search RBAC roles in `roleassignments.bicep` (`Search Index Data Contributor` for Functions + API/dev user or `Search Index Data Reader` for API; negative-test with 10-min propagation window!).
3. Implement `Ai:VectorStore:Provider=AzureAISearch` behind `IDocumentVectorStore` (1536-dim vector field, hybrid keyword + vector + semantic ranker, `EventId` filter security trimming, idempotent upsert by document key).
4. Wire `Ai__Enabled=true` + `Ai__AzureOpenAI__*` + `Ai__Search__*` in `appservice.bicep` & `functionapp.bicep`, deploy API + Functions, run re-index command, embedding-model-change drill, and local vs Azure eval comparison.
