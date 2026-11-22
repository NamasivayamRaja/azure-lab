# Cloud Week 2 — Day 4 Handoff (Document Intelligence, Async OCR Seam, Extraction Comparison & AI Design Notes)

Date: 2026-11-21 | Status: **DONE** | `Ai:Enabled` in Azure at EOD: **Pending Search deletion** *(delete `srch-azurelab-dev-cin-jyqvnq` now to stop ~\$0.101/hr billing; keep `di-azurelab-dev-cin-jyqvnq`, `cs-azurelab-dev-sin-jyqvnq`, and `oai-azurelab-dev-sin-jyqvnq` at \$0/hr idle)*

---

## 1. Resources Running in Azure & Spend (`rg-azurelab-ndvd-dev2-cin`)

| Resource | Name | Region | SKU / Settings | Hourly Cost | Status |
|---|---|---|---|---|---|
| **Azure AI Document Intelligence** | `di-azurelab-dev-cin-jyqvnq` | `centralindia` | `kind: FormRecognizer`, `S0`, `disableLocalAuth: true`, `customSubDomainName`, `publicNetworkAccess: Enabled` | **\$0.00/hr idle** | **NEW (Kept)** |
| Azure AI Content Safety | `cs-azurelab-dev-sin-jyqvnq` | `southindia` | `S0`, `disableLocalAuth: true`, `customSubDomainName` | **\$0.00/hr idle** | **Kept** |
| Azure OpenAI | `oai-azurelab-dev-sin-jyqvnq` | `southindia` | `S0` (`gpt-5-mini` + `text-embedding-3-small`) + `alert-openai-http-429` | **\$0.00/hr idle** | **Kept** |
| Azure AI Search | `srch-azurelab-dev-cin-jyqvnq` | `centralindia` | `basic` (`1R×1P`), `disableLocalAuth: true`, `semanticSearch: free` | **~\$0.101/hr** | **DELETE AT DAY 4 EOD** *(recreate on Day 5)* |
| `dev2` Baseline | App Service Plan (`P0v3`), SQL Serverless, SB Standard, Storage, Logs, App Insights | `centralindia` | Functions wired with `Ai__DocumentIntelligence__Provider=Azure`, `ModelId=prebuilt-layout` | ~\$2.40/day | **Kept** |

- **Day 4 incremental AI spend:** ~\$0.35 (AI Search ~3 hrs + Document Intelligence ~10 test pages <\$0.05 + OpenAI <\$0.01).
- **Estimated credit remaining:** **~\$178.15 of \$200** (~20 days left).

**RBAC Added (`di-azurelab-dev-cin-jyqvnq`):**
- `func-azurelab-dev-cin-jyqvnq` (MI) + Dev User (`AZURELAB_SQL_ADMIN_OID`): **`Cognitive Services User`** (`a97b65f3-24c7-4388-baec-2e87135dc908`). **API and Worker get none** (least privilege).
- *Parking lot item logged ([week2-parking-lot.md](file:///d:/2026/AzureLabs/docs/week2-parking-lot.md)):* Replace per-user Dev OIDs with an **Entra Security Group** (`principalType: 'Group'`) on Day 7.

---

## 2. Key Decisions & Code Artifacts

- **Document Intelligence vs Content Understanding:** Chose **Document Intelligence (`S0`, `prebuilt-layout`, GA v4.0 REST `2024-11-30`)** in `centralindia` over Content Understanding. Why: GA, \$0/hr idle, stays in `centralindia` (passes `allowed-locations-cin`), fixed per-page pricing (`prebuilt-read` \$1.50/1K pages, `prebuilt-layout` \$10.00/1K pages) with structured Markdown/HTML tables for RAG without extra Foundry LLM token fees at ingestion time.
- **Async `IDocumentTextExtractor` seam (0 new NuGet packages):**
  - Updated [IngestionModels.cs](file:///d:/2026/AzureLabs/AzureLab.Rag/Ingestion/IngestionModels.cs) (`Task<DocumentExtractionResult> ExtractTextAsync(Stream, string, CancellationToken)` returning `(Text, PageCount)`).
  - Kept local [PdfTextExtractor.cs](file:///d:/2026/AzureLabs/AzureLab.Rag/Ingestion/PdfTextExtractor.cs) and [DocxTextExtractor.cs](file:///d:/2026/AzureLabs/AzureLab.Rag/Ingestion/DocxTextExtractor.cs) intact.
  - Added [AzureDocumentIntelligenceExtractor.cs](file:///d:/2026/AzureLabs/AzureLab.Rag/Providers/DocumentIntelligence/AzureDocumentIntelligenceExtractor.cs) supporting `.pdf`, `.docx`, `.png`, `.jpg`, `.jpeg` via keyless `TokenCredential` + `HttpClient`.
  - **LRO vs 1-min Service Bus lock:** `document-processed` lock is 1 min; [host.json](file:///d:/2026/AzureLabs/AzureLab.Functions/host.json) sets `maxAutoLockRenewalDuration: 00:05:00`. `AiOptionsValidator` enforces `TimeoutSeconds` in `10..240s` (default `120s`) so polling never outlives lock renewal.
  - **Permanent vs transient errors:** `400`/`415`/`422` & LRO `InvalidContent`/`PasswordProtected` throw `PermanentIngestionException` -> immediate DLQ; `429` (honoring `Retry-After`), `5xx`, and timeouts escape -> Service Bus retry -> DLQ after `MaxDeliveryCount`.
- **Bicep:** [docintelligence.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/docintelligence.bicep), [main.bicep](file:///d:/2026/AzureLabs/infra/bicep/main.bicep), [roleassignments.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/roleassignments.bicep), [functionapp.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/functionapp.bicep), [dev.bicepparam](file:///d:/2026/AzureLabs/infra/bicep/params/dev.bicepparam).
- **AI Design Notes:** [docs/ai-notes.md](file:///d:/2026/AzureLabs/docs/ai-notes.md) (7 sections: Foundry Agent Service vs `AssistantService`, MCP & security, Foundry IQ agentic retrieval, RAG vs fine-tuning vs prompting, semantic caching, PII data-flow, 8-service selection table).

---

## 3. Local vs Azure Extraction Comparison & Live E2E Evidence

| File | Extractor | Pages | Chars | Latency (ms) | Cost / Page | Retrieval Impact |
|---|---|---:|---:|---:|---:|---|
| `scanned-schedule.pdf` | Local (`PdfPig`) | 1 | **0** | 665 | \$0.0000 | `SkippedNoText` (**0% recall**) |
| `scanned-schedule.pdf` | Azure `prebuilt-read` | 1 | 208 | 9,790 *(cold)* | \$0.0015 | 100% text extracted (flat) |
| `scanned-schedule.pdf` | Azure `prebuilt-layout` | 1 | 211 | 4,702 | \$0.0100 | 100% text + Markdown headings |
| `badge-policy.png` | Local (`PdfPig`/`OpenXml`) | 0 | **0** | 0 | \$0.0000 | `SkippedUnsupportedType` (**0% recall**) |
| `badge-policy.png` | Azure `prebuilt-read` | 1 | 253 | 4,874 | \$0.0015 | 100% OCR extracted |
| `badge-policy.png` | Azure `prebuilt-layout` | 1 | 263 | 4,655 | \$0.0100 | 100% OCR + numbered list |
| `pricing-table.docx` | Local (`OpenXml`) | 1 | 229 | 2,057 | \$0.0000 | Cells flattened into disconnected lines (loses column↔row mapping) |
| `pricing-table.docx` | Azure `prebuilt-read` | 1 | 203 | 4,445 | \$0.0015 | Flat text (same structure loss as Local) |
| `pricing-table.docx` | Azure `prebuilt-layout` | 1 | **452** | 4,464 | \$0.0100 | Preserves `<table><tr><td>` grid (row↔column bindings survive chunking) |

- **Live Azure E2E Verification (`badge-policy.png`):**
  - Uploaded `sample-docs/badge-policy.png` (`image/png`) to `event-docs/781ffe26-1158-4b9a-ae44-efe1f3541488/badge-policy.png` via Entra ID (`--auth-mode login`).
  - `POST /events/781ffe26-1158-4b9a-ae44-efe1f3541488/ask` (`"Where does the airport shuttle depart from?"`) returned:
    - `answered: true`, `answer: "The airport shuttle departs from Gate 4 [1]."`, `blobName: "781ffe26-1158-4b9a-ae44-efe1f3541488/badge-policy.png"`, `similarity: 0.68`, `outcome: "Answered"`.
- **RBAC Negative Test Note:** Removing `Cognitive Services User` from the dev user still succeeded locally because `DefaultAzureCredential` picked up an SP credential / RBAC cache; restored via Bicep.

---

## 4. What Day 5 Needs (Network Isolation)
1. **Recreate AI Search** via Bicep when Day 5 starts (`deploySearch=true`, `aiEnabled=true`).
2. **VNet, VNet Integration, NSG, Private Endpoints + Private DNS Zones** for Storage (blob, queue **and table** for Functions host storage), Azure SQL, Azure OpenAI, and AI Search; turn `publicNetworkAccess: Disabled` on all data/AI services.
3. **Private SQL migration & grant path** (how EF Core migrations and `CREATE USER ... FROM EXTERNAL PROVIDER` run once SQL has no public endpoint).
4. **DNS debugging drill** (`nslookup` / `tcpping` from App Service Kudu/SSH verifying Private DNS resolution `10.x.x.x` vs public IP).
