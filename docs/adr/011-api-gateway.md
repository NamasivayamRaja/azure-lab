# ADR-011: API Gateway & AI Gateway Selection (APIM vs Alternatives)

**Status:** Accepted (Lab-validated on Week 2, Day 6; APIM torn down at EOD to preserve credits)  
**Date:** 2026-11-22  
**Context:** `AzureLabs.Api` (`app-azurelab-api-dev-cin-jyqvnq`, App Service `P0v3`) and `oai-azurelab-dev-sin-jyqvnq` (Azure OpenAI, private-only via `pe-oai-azurelab-dev-cin`) need centralized edge auth, per-caller rate/token limiting, observability, and backend lock-down without distributing keys.

---

## 1. Gateway Options Comparison

| Option | Primary Role | Strengths | Limitations | When to Choose |
|---|---|---|---|---|
| **Azure API Management (APIM)** | Full API & AI gateway (L7) | OpenAPI contract governance, `validate-jwt`, per-caller `rate-limit-by-key`, native AI gateway (`llm-token-limit`, `llm-emit-token-metric`, keyless MI swap), W3C tracing, VNet outbound integration (`Standard v2`+) | Cost (`Basic v2` ~\$150/mo, `Standard v2` ~\$700/mo); distributed counter sync is eventually consistent across nodes; XML policy DSL needs disciplined review | Multi-consumer APIs, enterprise API governance, and **AI Gateway** (per-caller TPM budgets + keyless OpenAI mediation) |
| **Azure Front Door (AFD)** | Global L7 CDN + WAF + anycast routing | Global edge POPs, DDoS/WAF, multi-region active-active failover, Private Link origins (`Premium` ~\$330/mo+) | No per-caller JWT/OID rate limiting, no OpenAPI contract or LLM token policies | Global users, multi-region HA, WAF/CDN in front of APIM or App Service |
| **Azure Application Gateway** | Regional L7 load balancer + WAF inside VNet | VNet-native ingress, OWASP WAF, TLS termination, path/host routing, Private Endpoint / internal backends (~\$200–\$300/mo) | No API lifecycle, no JWT claim extraction, no per-`oid` or LLM token limits | Regional VNet ingress + WAF for web apps or in front of APIM internal mode |
| **YARP (.NET Reverse Proxy)** | Code-first L7 proxy in C# | Full .NET/C# control, runs on existing App Service/ACA (\$0 extra PaaS fee), exact custom rate-limit/auth logic, familiar DI/OTel | You own scaling, patching, HA, OpenAPI portal, and custom token-counting code | Small/medium .NET teams needing custom BFF/routing without APIM's \$700/mo Standard v2 cost |
| **No Gateway (Direct to App Service)** | Direct client → ASP.NET Core | Zero extra hop latency, \$0 gateway cost, simplest ops | No centralized per-caller throttling across multiple APIs/LLM consumers; every backend must enforce its own edge limits and expose a public endpoint | Single-app MVP or internal-only service where ASP.NET Core `RateLimiter` + Entra JWT suffice |

**Decision for AzureLab:** Use **APIM `Standard v2`** when running a multi-consumer API + shared Azure OpenAI AI Gateway over Private Endpoints. In production at global scale, layer **Front Door (WAF/global edge) → APIM Standard v2 (API/AI policies + VNet integration) → Locked App Service & Private OpenAI**.

---

## 2. Tier & Cost Trade-offs (APIM v2 in `centralindia`)

- **Basic v2 (~\$0.205/hr, ~\$150/mo):** Fast provisioning (~5–10 min), supports AI gateway policies (`llm-token-limit`), **no outbound VNet integration and no inbound Private Endpoint**. Cannot reach private-only OpenAI (`publicNetworkAccess: Disabled`) or lock App Service by subnet.
- **Standard v2 (~\$0.959/hr, ~\$700/mo):** Adds **outbound VNet integration** (delegated `Microsoft.Web/serverFarms` subnet ≥ `/27` with NSG) and **inbound Private Endpoint**. Reaches `pe-oai-azurelab-dev-cin` (`10.10.1.8`) privately and locks App Service via `Microsoft.Web` Service Endpoint + Access Restrictions.
- **Premium v2 (~\$2,800+/mo):** Full VNet injection (inbound + outbound inside VNet), multi-region, higher scale. Overkill unless compliance requires zero shared control/data plane.

---

## 3. Latency of the Extra Hop

- Adding APIM in the same region (`centralindia`) adds **~5–15 ms** p50 TLS + policy evaluation overhead (measured ~10–20 ms for `/api/offers/.../redeem` and negligible vs ~1,400 ms `gpt-5-mini` inference).
- For **API → APIM → Private OpenAI**, keep APIM in the **same region** as the API (`centralindia`) so the extra hop stays single-digit ms; cross-region Private Link (`centralindia` → `southindia` OpenAI) already dominates RTT.

---

## 4. Policy Ownership

- **Platform / Cloud Engineering** owns: global (`All APIs`) policies (CORS defaults, TLS, W3C tracing, security headers, base `validate-jwt` templates), VNet integration, NSGs, and shared AI Gateway quotas (`llm-token-limit`, `llm-emit-token-metric`).
- **Application / Product Teams** own: OpenAPI specs (`azurelab-api.openapi.json`), per-operation overrides (e.g., `/health` exempt from JWT), and backend defense-in-depth authorization (`ApiAccess` roles/scopes, per-event tenant checks).
- **Governance rule:** All policies live in Git (`infra/bicep/apim/*.xml`) and deploy via Bicep/CI — never hand-edited in the portal.

---

## 5. Gateway Placement Relative to the Private Network

- **Public-edge / Private-backend (what we built):** APIM `Standard v2` uses `virtualNetworkType: External` — its inbound gateway (`*.azure-api.net`) is internet-facing, while outbound traffic enters `vnet-azurelab-dev-cin/snet-apim-int` (`10.10.2.0/26`) to reach:
  1. `app-azurelab-api-dev-cin-jyqvnq` via `Microsoft.Web` Service Endpoint + `Deny`-default Access Restrictions (blocking direct internet bypass with `403`).
  2. `oai-azurelab-dev-sin-jyqvnq` privately via `pe-oai-azurelab-dev-cin` (`10.10.1.8`) + `privatelink.openai.azure.com` (permitted by `nsg-snet-pe` rule `allow-apim-https`).
- **Fully internal variant:** Add an **inbound Private Endpoint** on APIM Standard v2 (or Front Door Premium Private Link Origin) if no direct internet access to APIM is allowed.

---

## 6. What I Would NOT Put in a Gateway Policy

1. **Business logic or domain rules** (e.g., offer inventory checks, outbox writes, RAG prompt assembly, EventId security trimming) — untestable XML/C# expressions become an unmaintainable shadow monolith.
2. **Database or complex stateful orchestration** (multi-step sagas, joins, transactions) — belongs in ASP.NET Core / Worker services with unit tests and EF Core.
3. **Fine-grained resource authorization** (e.g., "does user `oid` belong to `EventId` X?") — keep coarse JWT signature/issuer/audience checks at APIM and domain authorization inside the API (defense in depth).
4. **Heavy payload transformations or PII redaction pipelines** — hard to version/test in XML and burns gateway CPU; keep payloads pass-through and do structured transformations in application code.
5. **Hardcoded secrets or API keys** — always use Managed Identity (`authentication-managed-identity`) or Key Vault-backed Named Values.

---

## 7. Revisit When

- **Strict global rate/token counter accuracy is required:** Multi-node `Standard v2` counters are eventually consistent; attach an external Azure Managed Redis cache to APIM when exact global limits are mandatory.
- **Global WAF/CDN or private-only ingress is required:** Layer Azure Front Door (WAF/anycast) in front of APIM, or attach an inbound Private Endpoint to APIM `Standard v2`.
- **Single-app cost optimization:** If only one internal app consumes the API and OpenAI deployment, retire APIM `Standard v2` (saving ~\$700/mo) and enforce rate/token limits in ASP.NET Core middleware.
