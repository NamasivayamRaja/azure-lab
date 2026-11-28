# ADR 006: Azure AI Region, Model Selection, and Policy Strategy

- **Status:** Accepted (Validated across Cloud Week 2, Days 0–6)
- **Context:** The kept Week 1 baseline (`rg-azurelab-ndvd-dev2-cin`) and subscription policy (`allowed-locations-cin`) are locked to `centralindia`. Day 0 read-only quota verification showed `centralindia` has `0` pay-as-you-go Azure OpenAI quota (only `GlobalProvisionedManaged` PTUs, no embeddings), whereas `southindia` and `eastus2` have active pay-per-token chat and embedding quota. Week 2 requires Azure OpenAI, AI Search (Basic + Semantic Ranker), Content Safety (Prompt Shields), Document Intelligence, and Day 5 Private Endpoint isolation within ~\$183.51 remaining Free Trial credit (22 days left).

## Decision
1. **Split-Region Placement (Option A):**
   - **Azure OpenAI / Foundry (`southindia`):** Deploy `gpt-5-mini` (`2025-08-07`, `GlobalStandard`, 500K TPM quota) for chat/RAG and `text-embedding-3-small` (`1`, `DataZoneStandard` or `GlobalStandard`, 1M TPM quota; retires `2028-02-09`) for embeddings. Avoid `gpt-4o` / `gpt-4.1` (retiring `2027-04-14`).
   - **All Other Services (`centralindia`):** Keep `dev2` compute/data (`App Service`, `SQL`, `Service Bus`, `Storage`) plus new Week 2 services (**AI Search Basic**, **Content Safety**, **Document Intelligence**) in `centralindia` (all confirmed GA in `centralindia` via Microsoft Learn; AI Search also verified via `Microsoft.Search` provider).
2. **Policy Strategy (`allowed-locations-cin`):**
   - Amend the subscription `allowed-locations-cin` assignment to allow `['centralindia', 'southindia']` (preferred over a one-off exemption for reproducible Bicep deploys; implemented on Day 1 with user approval).
3. **Private Networking (Day 5 Cross-Region PE):**
   - Keep the VNet and all Private Endpoints in `centralindia`. Azure Private Link supports cross-region targets: a Private Endpoint in the `centralindia` VNet connects to the `southindia` Azure OpenAI account over the Microsoft backbone via `privatelink.openai.azure.com`.
4. **SKU & Cost Guardrails:**
   - **Identity-only:** `disableLocalAuth = true` on every AI resource; zero API keys.
   - **AI Search Basic** (\$73.73/mo, \$0.101/hr): Free tier lacks semantic ranker, MI auth, and Private Endpoints. Create late, delete same day.
   - **Content Safety & Document Intelligence:** Use `S0` (pay-per-call, \$0 idle) when >2-page OCR or Private Endpoints are needed (`F0` lacks Private Endpoints and caps Doc Intelligence at 2 pages).
   - **Service Bus Premium** (\$0.928/MU/hr, ~\$677/mo): Dropped as a live lab; documented in design notes only.

## Alternatives Considered
- **Option B — Azure OpenAI in `eastus2`:** Rejected. Adds ~200–250 ms cross-continent RTT vs ~15–25 ms (`centralindia` ↔ `southindia`) and moves data-at-rest outside India; `southindia` already provides identical `gpt-5-mini` (500K TPM) and `text-embedding-3-small` (1M TPM) quota.
- **Option C — Stay Strictly in `centralindia` (Non-OpenAI / NO-GO):** Rejected. `centralindia` has no pay-as-you-go OpenAI quota; would force dropping the core Azure OpenAI Day 1–3 implementation.

## Consequences
- **Positive:** Unblocks Week 2 on an active model family (`gpt-5-mini`, avoiding 2027-04-14 `gpt-4o`/`4.1` retirement), keeps data-at-rest in India, preserves low RTT (~15–25 ms), and fits comfortably inside the \$183.51 credit (~ \$25–\$35 projected Week 2 total).
- **Negative / Trade-offs:**
  - `GlobalStandard` (`gpt-5-mini`) allows inference processing outside India (data at rest stays in `southindia`); strict single-region inference compliance would require `Standard` regional quota in a supported region or PTUs.
  - Cross-region OpenAI requires amending `allowed-locations-cin` and adds minor inter-region PE data charges on Day 5.
  - **Lesson learned (captured in `docs/interview-prep/learning-notes.md`):** Always choose the landing zone primary region against the most constrained *planned* service (AI model quotas), not just Phase 1 compute/data.

## Revisit When
- Microsoft releases pay-as-you-go (`Standard` or `GlobalStandard`) quota for `gpt-5-mini` and `text-embedding-3-small` in `centralindia` (allowing single-region consolidation).
- Regulatory compliance mandates strict in-country *inference* processing (requiring migration from `GlobalStandard` to regional `Standard` or `DataZoneStandard` deployments).
- Sustained token volume exceeds the break-even threshold for Provisioned Throughput Units (`GlobalProvisionedManaged` PTUs).
