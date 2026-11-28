# ADR 010: API Gateway & AI Gateway Selection (APIM vs Alternatives)

- **Status:** Accepted (Lab-validated on Week 2, Day 6; APIM torn down at EOD to preserve credits — also cross-referenced as [011-api-gateway.md](./011-api-gateway.md))
- **Date:** 2026-11-22 (Updated 2026-11-26)
- **Context:** `AzureLabs.Api` (`app-azurelab-api-dev-cin-jyqvnq`, App Service `P0v3`) and `oai-azurelab-dev-sin-jyqvnq` (Azure OpenAI, private-only via `pe-oai-azurelab-dev-cin`) need centralized edge authentication, per-caller rate and token limiting, W3C distributed trace continuity, and backend lock-down without distributing API keys.

## Decision
1. **Gateway & Tier Selection — Azure API Management `Standard v2` (`apim-azurelab-dev-cin-jyqvnq`):**
   - Use **APIM `Standard v2`** (~\$0.959/hr, ~\$700/mo) with outbound VNet integration into `vnet-azurelab-dev-cin/snet-apim-int` (`10.10.2.0/26`, delegated `Microsoft.Web/serverFarms` with required `nsg-snet-apim-int`).
2. **API Perimeter & Backend Lock-Down:**
   - Enforce `validate-jwt` (Entra ID `api://<clientId>`), `rate-limit-by-key` (`5` calls/`60s` per `oid`), `validate-content` (`max-size="65536"` → HTTP `400`), and `X-Correlation-ID` / W3C `traceparent` preservation at APIM (exempting `/api/health` and `/api/health/ready` from JWT/rate limits).
   - Keep `Microsoft.Identity.Web` JWT validation and `ApiAccess` / `EventEnrollments` authorization inside ASP.NET Core for defense in depth.
   - Lock `app-azurelab-api-dev-cin-jyqvnq` (both `production` and `staging` slots) to `snet-apim-int` via `Microsoft.Web` Service Endpoint (`ipSecurityRestrictionsDefaultAction: 'Deny'`, `scmIpSecurityRestrictionsUseMain: false`), blocking direct bypass with **`403 Ip Forbidden`** at the App Service front-end proxy while preserving internal scale-unit Health Checks and slot warm-up.
3. **Keyless AI Gateway (`/openai-gw`):**
   - Point `Ai__AzureOpenAI__Endpoint` to `https://apim-azurelab-dev-cin-jyqvnq.azure-api.net/openai-gw` (zero C# changes).
   - APIM validates the calling app's Entra MI token (`aud: https://cognitiveservices.azure.com`), enforces per-caller `<llm-token-limit>` (`200` TPM per `oid`, `estimate-prompt-tokens="true"`) and `<llm-emit-token-metric>` to Application Insights, swaps to APIM's own System-Assigned MI (`authentication-managed-identity`, `Cognitive Services OpenAI User`), and forwards privately to `10.10.1.8` (`pe-oai-azurelab-dev-cin`).
4. **Policy Guardrails (What Never Goes in APIM):**
   - Never place domain business logic, DB orchestration, fine-grained per-`EventId` authorization, heavy payload transforms, or hardcoded secrets inside APIM XML policies.

## Alternatives Considered
- **Option A — APIM `Basic v2` (~\$0.205/hr, ~\$150/mo):** Rejected. Lacks outbound VNet integration and inbound Private Endpoints, so it cannot reach private-only Azure OpenAI (`10.10.1.8`) or lock App Service via a VNet Service Endpoint.
- **Option B — Azure Front Door or Application Gateway Alone:** Rejected as a replacement for APIM. Front Door provides global anycast CDN/WAF/failover and App Gateway provides regional VNet L7 LB/WAF, but neither supports OpenAPI contract lifecycle, JWT `oid` claim extraction for per-caller rate limits, or AI Gateway `llm-token-limit` policies (in global production, layer Front Door in front of APIM).
- **Option C — YARP (.NET Reverse Proxy) or No Gateway (Direct to App Service):** Valid for a single internal app where `Standard v2`'s ~\$700/mo cost is unjustified, but lacks managed multi-consumer API governance and turn-key multi-app OpenAI token mediation.

## Trade-offs Accepted & Consequences
- **Positive:** Unauthenticated requests (`401`), oversized payloads (`400`), and noisy-neighbor LLM callers (`429` with `Retry-After`) are stopped at the edge with zero backend compute or OpenAI TPM impact; full W3C `OperationId` continuity from APIM → API → Service Bus → Worker.
- **Negative / Trade-offs:**
  - High fixed cost (`Standard v2` ~\$0.959/hr ≈ \$700/mo; toggled off via `deployApim = false` in dev between sessions) and ~5–15 ms same-region hop latency.
  - Multi-node `Standard v2` rate/token counters are eventually consistent (allowing 7–8 calls on a `5/60s` burst limit unless backed by external Redis).

## Revisit When
- Strict global rate/token counter accuracy is required across APIM scale-out nodes (attach an external Azure Managed Redis cache to APIM).
- Public internet ingress must be fronted by a global WAF/DDoS edge (place Azure Front Door Premium in front of APIM) or APIM must be purely internal (enable an inbound Private Endpoint on APIM `Standard v2` or upgrade to `Premium v2`).
- Workload remains a single first-party app without external consumers or shared OpenAI deployments (remove APIM to save ~\$700/mo and rely on ASP.NET Core `RateLimiter` + Private Endpoints).
