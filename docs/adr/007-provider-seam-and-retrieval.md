# ADR 007: AI Provider Seams, Hybrid+Semantic Retrieval, and Index Lifecycle

- **Status:** Accepted (Validated on Cloud Week 2, Days 1–4)
- **Date:** 2026-11-26
- **Context:** `AzureLab` runs locally against `Ollama` (`qwen2.5:3b-instruct`, `nomic-embed-text` 768-dim), PostgreSQL/`pgvector`, and local PDF/DOCX extraction (`PdfPig`/`OpenXml`), while in Azure it must run against `Azure OpenAI` (`gpt-5-mini`, `text-embedding-3-small` 1536-dim), `Azure AI Search` (`Basic`), and `Azure AI Document Intelligence` (`S0`) without code forks in business logic, without leaking data across events, and with zero-downtime index migrations when embedding dimensions change.

## Decision
1. **Config-Driven Provider Seams (Zero Business-Logic Branches):**
   - Keep all AI/RAG integrations behind four clean ports selected solely via configuration (`Ai:Provider`, `Ai:VectorStore:Provider`, `Ai:DocumentIntelligence:Enabled`):
     - `IChatClient` (`Microsoft.Extensions.AI`) with a parameter-sanitizing middleware that strips unsupported `temperature` for reasoning models (`gpt-5-mini`).
     - `ITextEmbedder` (`768`-dim local vs `1536`-dim Azure).
     - `IDocumentVectorStore` (`PostgresVectorStore` locally vs `AzureAiSearchVectorStore` in Azure).
     - `IDocumentTextExtractor` (async seam: `CompositeLocalTextExtractor` vs `AzureDocumentIntelligenceExtractor` `prebuilt-layout` LRO for OCR/images/tables).
2. **Retrieval Mode — `HybridSemantic` (Vector + BM25 + L2 Semantic Ranker):**
   - Configure Azure AI Search with HNSW vector search (`text-embedding-3-small`, 1536-dim), BM25 keyword search, Reciprocal Rank Fusion (RRF), and the **Semantic Ranker (`sem-default`)** L2 cross-encoder (`[0, 4]` calibrated `RerankerScore`, normalized to `[0, 1]` against `MinSimilarity = 0.45`).
3. **Security Trimming & Injection Prevention:**
   - Authorize `(oid, eventId)` against `EventEnrollments` *before* calling Search (`403 Forbidden`), then pass a strongly typed `Guid` into `SearchFilter.Create($"eventId eq {eventId:D}")` so OData filter injection is impossible.
4. **Idempotent Ingestion & Blue/Green Index Migration:**
   - Generate deterministic hex chunk keys `LowerHex(SHA256("{DocumentKey}|{ChunkIndex}"))`, check `sourceVersionUtc` to ignore superseded revisions, and `MergeOrUpload` new chunks *before* deleting stale chunk keys.
   - Keep index creation/migration out of runtime apps (API has `Search Index Data Reader`; Functions has `Search Index Data Contributor`). Use `AzureLab.SearchTool` (with `Search Service Contributor`) to create versioned indexes (`azurelab-docs-v2`), re-index from source blobs + `EventDocuments`, and swap the alias (`azurelab-docs → azurelab-docs-v2`) before deleting `v1`.

## Alternatives Considered
- **Option A — `VectorOnly` or `Hybrid` (RRF without Semantic Ranker):** Rejected. In our 12-question eval, RRF scores measure rank agreement rather than absolute relevance, so all 3 unanswerable questions (`"dress code"`, `"lunch"`, `"capital of France"`) passed the threshold (`0.51` normalized) and burned LLM tokens (`5,002 in / 3,480 out`, `avgGenerateMs ~3.6s`). `HybridSemantic` rejected all 3 (`0.44`, `0.35`, `0.15` < `0.45`), cutting tokens by **>53%** (`2,340 in / 1,493 out`) and latency to **`1,903 ms`**.
- **Option B — PostgreSQL + `pgvector` on Azure Database for PostgreSQL:** Rejected for Week 2 scope. Adds a second relational database alongside Azure SQL and lacks a turn-key managed L2 cross-encoder equivalent to Azure AI Search's Semantic Ranker.
- **Option C — Auto-creating/updating the Search index on API/Functions startup:** Rejected. Grants runtime identities control-plane `Search Service Contributor` rights and risks concurrent startup race conditions across instances/slots.

## Trade-offs Accepted & Consequences
- **Positive:** Identical domain/application code across local dev (\$0) and Azure; measured **>53% token reduction** on off-topic queries; zero-downtime embedding-model upgrades via index aliases.
- **Negative / Trade-offs:**
  - AI Search `Basic` costs ~\$0.101/hr (~\$73.73/mo) and Semantic Ranker free quota is capped at 1,000 requests/month before requiring the `standard` semantic tier.
  - Because AI Search lacks multi-chunk ACID transactions, ingestion must explicitly order `MergeOrUpload` before stale-chunk deletion.

## Revisit When
- Semantic Ranker query volume exceeds 1,000 queries/month (upgrade `semanticSearch` from `free` to `standard`) or corpus size exceeds `Basic` tier storage/partition limits.
- Per-document ACLs grow finer than `EventId` (e.g., role- or user-specific document permissions requiring security-principal group filters on every chunk).
