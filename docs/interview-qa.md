# Cloud Week 1 — Interview Q&A

## Day 2: Managed Identity & Least-Privilege RBAC

1. **Managed Identity (MI) vs. Service Principal (SP)?**
   A Managed Identity is an Entra-managed Service Principal tied to an Azure resource's lifecycle with automatic credential rotation and zero client secrets or certificates to store or rotate.

2. **System-Assigned vs. User-Assigned Managed Identity?**
   System-assigned is 1:1 with a single resource (auto-deleted with it, and staging slots get their own separate principal); User-assigned is a standalone resource shareable across multiple apps and pre-assignable before deployment.

3. **Why isn't Subscription `Owner` enough to read Blob data?**
   `Owner` grants `*` on control-plane `actions` (ARM), but `dataActions` is empty—when `allowSharedKeyAccess=false`, `Owner` still gets `403 AuthorizationPermissionMismatch` on the Blob data plane without a data role like `Storage Blob Data Reader/Contributor`.

4. **How does RBAC propagation affect incident troubleshooting?**
   Azure RBAC additions/removals take ~1–5+ minutes to propagate to data-plane caches, and apps also cache MI tokens (~24h), user delegation keys (2h), and AMQP links—so restarting the app or waiting out the cache is required to see a change take effect.

5. **Account-Key SAS vs. User Delegation SAS?**
   An Account-Key SAS is signed with the storage account's master key (harder to revoke, broad blast radius). A User Delegation SAS is signed with an Entra user-delegation key (`skoid`), and at request time Storage enforces the intersection of the SAS `sp` permissions and the delegator's live RBAC roles.

6. **How do you prove least privilege in production?**
   Combine an RBAC inventory (narrowest scope: container/queue/subscription, narrowest role: `Contributor` over `Owner`), negative testing in dev (revoke each role and record the exact failure), no-secrets control-plane locks (`allowSharedKeyAccess=false`, `disableLocalAuth=true`, `azureAdOnlyAuthentication=true`), and `what-if` drift checks.

7. **What are the pitfalls of `DefaultAzureCredential` in production?**
   Stray `AZURE_*` environment variables can hijack `EnvironmentCredential`, failed MI probes fall through to slow developer-tool credentials (masking the real MI error), and multiple User-Assigned MIs require an explicit `ManagedIdentityClientId`.

8. **How do you detect RBAC failures and permission drift?**
   Query resource data-plane logs (`StorageBlobLogs` for `403`, Service Bus `AzureDiagnostics`) for denied requests (Entra MI sign-in logs only show token issuance), monitor Activity Logs for `Microsoft.Authorization/roleAssignments/*`, and run `az deployment group what-if` against Bicep.


## Day 3 — CI/CD, OIDC & Safe Releases

1. **Why does OIDC beat a stored client secret in GitHub Actions?**
   OIDC issues a short-lived, cryptographically signed JWT per job run—no static secret is stored in GitHub, there is zero secret rotation, and Entra validates the exact repo, event, and environment claims.

2. **How do you design OIDC subject (`sub`) claims, and how does a mismatch fail?**
   Scope deployments to Environments (`repo:<owner>@<id>/<repo>@<id>:environment:dev|prod`) and PR previews to `pull_request` (`repo:...:pull_request`). Any mismatch in event, environment, repo casing, or immutable IDs fails `azure/login` with `AADSTS700213: No matching federated identity record found for presented assertion subject '...'`.

3. **Why is Bicep `what-if` a review gate rather than a guarantee?**
   `what-if` evaluates ARM state at planning time—runtime-only values (`listKeys`), provider false-positives (e.g., `diagnosticSettings`), or changes merged between PR review and deploy can still differ at execution time.

4. **How do App Service slot swaps work, and what happens to API background work?**
   Azure warms up the `staging` slot (with production's sticky settings applied during swap-with-preview) and atomically flips VIP routing only after warm-up returns `200`. Applying sticky settings recycles the swapped slot (`SIGTERM`), which is why APIs stay stateless and durable work lives in Outbox + Worker.

5. **Which settings must be sticky (`slotConfigNames`) vs non-sticky?**
   Only "where it runs" sticks to the slot (`ASPNETCORE_ENVIRONMENT`, `WEBSITE_SWAP_WARMUP_PING_PATH`, `WEBSITE_SWAP_WARMUP_PING_STATUSES`, and `AzureWebJobs.<Func>.Disabled` in Functions). "What the code needs" (`ConnectionStrings__*`, `ServiceBus__*`, `BlobStorage__*`, `WEBSITE_RUN_FROM_PACKAGE`) swaps with the code.

6. **How do you handle schema changes safely during a slot swap?**
   Run EF migrations (`efbundle`) from the pipeline identity (never app identities) *before* deploying/swapping, using Expand/Contract: Release N+1 expands (nullable columns/tables) so old (N) and new (N+1) slot code both work; Release N+2 contracts only after old code is gone.

7. **How do you roll back a bad release in App Service / Functions vs Worker?**
   For API and Functions, the previous production build remains in `staging`, so start `staging` and swap `staging` -> `production` in seconds. For Worker (no slot), revert the commit via PR -> `main` (re-deploying cleanly) while relying on Expand/Contract DB compatibility.

8. **Why is the Worker deployed without a staging slot, and what makes it safe?**
   A Worker staging slot would compete with production on Service Bus queues/topics and `dbo.OutboxMessages`. Direct deploy is safe because App Service sends `SIGTERM` for graceful drain, unfinished Service Bus peek-locks/30s Outbox leases expire, and idempotent handlers make redelivery a no-op.


## Day 4 — Authentication & Authorization (Entra ID / OAuth2 / OIDC)

1. **401 Unauthorized vs. 403 Forbidden?**
   `401` means **authentication failed** (no token, expired token, bad signature, wrong `aud`/`iss`)—the caller's identity is unknown or untrusted. `403` means **authentication succeeded**, but **authorization failed** (missing `scp`/`roles`, or caller `oid` lacks access to the requested `eventId`).

2. **Delegated Scopes (`scp`) vs. Application Roles (`roles`)?**
   Delegated scopes (`scp`) apply when a client acts **on behalf of a signed-in user** (`effective permissions = user ∩ app`). Application roles (`roles`) apply either to user/group role assignments or to **daemon/Managed Identity** callers (`idtyp: app`) acting as themselves with no user present.

3. **How do you validate Entra ID v2 access tokens correctly?**
   Use `Microsoft.Identity.Web` (`AddMicrosoftIdentityWebApi`) which validates four pillars: **signature** (RS256 via OIDC JWKS discovery), **issuer** (`https://login.microsoftonline.com/<tid>/v2.0`), **audience** (bare `ClientId` GUID in v2 tokens), and **lifetime** (`nbf`/`exp`). Never validate ID tokens on a resource API.

4. **What is IDOR / BOLA, and how did we fix it on `/redeem`?**
   Broken Object-Level Authorization (IDOR) occurs when an API trusts a caller-supplied identifier (e.g., `UserId` in `RedeemRequestDto`) instead of deriving identity from the verified token. We removed `UserId` from the request DTO and derived it from the token's immutable `oid` claim.

5. **App Service Easy Auth vs. In-code JWT Bearer (`Microsoft.Identity.Web`)?**
   Easy Auth runs as a zero-code reverse-proxy sidecar on Azure App Service, which is convenient for simple perimeter auth but Azure-locked, lacks local dev parity, and makes fine-grained policies/OBO awkward. In-code `Microsoft.Identity.Web` is portable, identical locally and in Azure, and integrates natively with ASP.NET Core `[Authorize]` policies and `IEventAccessService`.

6. **Why never trust a client-sent `userId` or `event_id` list, and why use `oid` instead of `email`/`sub`?**
   Request bodies and unsigned headers are attacker-controlled. `email`/`upn` are mutable (name changes), and `sub` in Entra v2 is pairwise (unique per client app), whereas `oid` (+ `tid` if multi-tenant) is immutable and consistent across all apps in the tenant.

7. **How does Managed Identity work for Service-to-Service calls?**
   Assign an **App Role** (e.g., `Api.Service`) on the API's Service Principal to the caller's Managed Identity via Graph `appRoleAssignments`. The caller acquires a token for `api://<api-client-id>/.default` using `ManagedIdentityCredential` (zero secrets), and Entra emits `"roles": ["Api.Service"]` and `"idtyp": "app"`.

8. **How do token lifetime and revocation work with stateless JWTs?**
   Access tokens are short-lived (~1 hour) and validated statelessly by signature/claims, so revoking a user or role doesn't invalidate an already-issued JWT until it expires. Keep access-token lifetimes short, rely on Entra Continuous Access Evaluation (CAE) for critical events (user disabled/password reset), and check live DB ACLs (`EventEnrollments`) for per-resource permissions.


---

## Day 5 — Observability, Distributed Tracing & Alerting

1. **How does distributed trace propagation work across an outbox and a message queue?**
   HTTP/in-memory `Activity.Current` ends when the API commits the DB transaction and returns `202`. To preserve the trace, `Outbox.Create` persists the W3C `traceparent`/`tracestate` into `OutboxMessage.Properties`, `OutboxDispatcher` restores it via `ActivityContext.TryParse` + `ActivitySource.StartActivity`, and `ServiceBusSender` injects `Diagnostic-Id` into `ApplicationProperties` so `ServiceBusProcessor` parents the consumer span to the same `TraceId`.

2. **What is the difference between `CorrelationId` and `TraceId` (`OperationId`)?**
   `TraceId` (`OperationId`) is a technical W3C distributed-tracing identifier linking parent-child spans across hosts in Application Insights. `CorrelationId` is a business/request identifier (`X-Correlation-ID`) stored in DB rows (`RedeemTransaction`, `OutboxMessage`), Service Bus messages, and structured logs for idempotency and business-level lookup across retries or separate traces.

3. **What are the trade-offs of telemetry sampling?**
   Head-based sampling (e.g., `traceidratio` at 10%) cuts ingestion cost and storage linearly, but drops rare errors if decided before the outcome is known. Keeping 100% of `Request`s (`excludedTypes: "Request"` in Functions `host.json`) or tail/adaptive sampling preserves failure visibility while trimming noisy `Dependency`/`Trace` volume.

4. **How do SLIs and SLOs relate to alerts?**
   An **SLI** (Service Level Indicator) is the measurable signal (e.g., `POST /redeem` 5xx rate, outbox dispatch lag < 60s, `/health/live` availability). An **SLO** is the target reliability threshold (e.g., 99.9% < 2s); alerts should fire on SLO-threatening symptoms with a clear runbook, not every transient resource spike.

5. **What separates an actionable alert from a noisy alert?**
   An actionable alert indicates user/data impact (`DeadletteredMessages > 0`, `Http5xx > 2`, outbox lag > 60s), uses multi-minute evaluation windows (`PT5M`–`PT15M`) to smooth blips, and pairs with a 5-line runbook. Noisy alerts fire on single-sample latency spikes or high CPU with no operator action.

6. **How do you debug a stuck message in Azure Service Bus?**
   Check `ActiveMessages` vs `DeadletteredMessages` on the entity, verify Worker `/health/ready` and running state, query `AppTraces` for `Abandon`/`MessageLockLost` with `DeliveryCount`, or `DlqTool peek` to inspect `DeadLetterReason` + `CorrelationId` and trace the `OperationId` in App Insights.

7. **How do you control observability cost without flying blind?**
   Set a Log Analytics daily ingestion cap (`dailyQuotaGb: 1`) and 30-day retention in Bicep, sample high-volume `Trace`/`Dependency` telemetry while keeping 100% of `Request`s/errors, avoid logging payloads/PII/blob names, and rely on low-cost platform metrics (`DeadletteredMessages`, `Http5xx`) for always-on alerting.


---

## Day 6 — Load Testing, Autoscale, Failures & Capacity Planning

1. **Scale out (horizontal) vs. scale up (vertical) — when do you choose each?**
   Scale **up** (e.g., `P0v3` -> `P1v3` or `GP_S_Gen5_1` -> `GP_Gen5_2`) when a single instance hits CPU/memory saturation (`98%` SQL CPU, `69%` plan RAM), connection-pool ceilings, or single-threaded bottlenecks. Scale **out** (adding instances) for high availability across fault domains and stateless horizontal throughput once per-instance bottlenecks are resolved.

2. **What causes autoscale lag and flapping, and how do you design against them?**
   Reactive autoscale has two lags: metric aggregation/evaluation (`PT5M` window -> `3m 16s` in our test) plus VM/container cold-boot and warm-up (`3m 45s` -> **`7m 03s` total**). Prevent **flapping** with asymmetric rules: fast scale-out (`PT5M`, OR logic) and slow scale-in (`PT10M` window + `PT10M` cooldown, AND logic, wide hysteresis gap like `>60%` out vs `<25%` in).

3. **What happens to in-flight Service Bus messages during scale-in or instance termination?**
   App Service Linux sends `SIGTERM` and waits **30 seconds** (`WEBSITES_SHUTDOWN_TIMEOUT`) before `SIGKILL`. If the handler finishes and commits SQL before `SIGKILL` (and completes with `CancellationToken.None`), the message is settled; if killed mid-flight or cancelled without `AbandonMessageAsync`, the broker holds the lock until `lockDuration` (`PT30S`) expires and redelivers (`DeliveryCount + 1`), where our atomic SQL transaction + `ProcessedMessages` inbox guarantees zero duplicates.

4. **What is plan-level autoscale coupling, and when do you split plans?**
   In App Service, autoscale targets the **App Service Plan (`serverfarms`)**, not individual apps. Because API, Worker, and Functions shared `asp-azurelab-dev-cin`, scaling `1 -> 2` doubled all 3 apps and their SQL connection pools simultaneously. In production, split `asp-api` (scales on HTTP CPU/latency, uses slots) from `asp-worker` (scales on Service Bus queue depth, no slots) to isolate noisy neighbors.

5. **Why do laptop-based load tests mislead, and how do you still get trustworthy numbers?**
   A developer laptop adds last-mile ISP RTT (`~22–30 ms`), TLS handshake overhead, Wi-Fi jitter, and NAT port limits—and by Little's Law ($L = \lambda W$), higher RTT artificially caps closed-loop throughput per VU. To stay honest at \$0, use keep-alive VUs on the laptop for moderate RPS and read **server-side `AppRequests.DurationMs`** in Application Insights as truth; move the generator into the same Azure region (ACI or Azure Load Testing) for peak RPS or sub-50ms SLA tests.

6. **What are the real trade-offs of Azure SQL Serverless?**
   Serverless bills per vCore-second (`0.5` min) and drops compute cost to \$0 when auto-paused (`15m` min delay), **but** any background poller (`OutboxDispatcher` every `2s`) keeps it awake 24/7 (burning `100,000` monthly free vCore-seconds in `2.31 days`). When it *does* pause, the first connection fails with **`Error 40613`** during a ~30–60s cold resume, requiring `Connection Timeout=60` and EF `EnableRetryOnFailure`.

7. **How do you capacity-plan from measured load data?**
   Measure saturation on 1 instance (`36.6 rps`, `98%` SQL CPU, `MaxConcurrentCalls=1` queue backlog), identify the first bottleneck (free config fixes like `MaxConcurrentCalls=8` + `BatchSize=50` before buying bigger SKUs), verify downstream ceilings (SQL max sessions/CPU, Service Bus Standard `1,000 credits/s`), compute marginal cost per 1,000 operations, and size headroom for peak × safety factor plus autoscale warm-up lag.


---

## Day 7 — Consolidation & Timed Mock Interview (Practice Status: Pending)

Use these 10 questions (drawn from Week 1) for a timed self-drill—aim for 2–3 minutes per answer with exact resource names, roles, failure modes, and measured numbers:

1. **[Whiteboard — File Upload]:** Walk through `event-docs` vs `bulk-player-enrollment` uploads: User-Delegation SAS without shared keys, quarantine copy/delete via Functions `BlobTrigger`, and exact RBAC roles/scopes.
2. **[Whiteboard — DLQ Spike in Production]:** `redeem-requests/$DeadLetterQueue` jumps to 450 messages at 2:00 AM. How do you triage poison vs transient outage, safely replay via `DlqTool`, and guarantee zero oversell or stuck rows?
3. **[Whiteboard — Zero-Downtime Deployment]:** Adding a non-nullable column to `RedeemTransactions` across API, Worker, and Functions: why Worker is slotless, how `Functions/staging` avoids trigger contention, and Expand/Contract `efbundle` ordering.
4. **RBAC Caching & Revocation:** Why did User-Delegation SAS generation survive role deletion until app restart, and how does Storage/Service Bus RBAC propagation differ from SQL `EXTERNAL USER` removal?
5. **In-Code JWT vs Easy Auth & Entra v2:** Why choose `Microsoft.Identity.Web` over Easy Auth, why omit `AzureAd:Audience` for v2 tokens, and how do `oid` + `EventEnrollments` eliminate BOLA/IDOR?
6. **Outbox Trace Propagation:** How does W3C `traceparent` survive the SQL Outbox boundary into Service Bus (`Diagnostic-Id`) without a schema change?
7. **Silent-Death Alerting:** Why did we choose a `scheduledQueryRules` KQL log alert over a Worker-emitted custom metric for outbox lag?
8. **Autoscale Coupling & Lag:** Why did `1 -> 2` scale-out take `7m 03s`, what happens to SQL connections when API/Worker/Functions share `asp-azurelab-dev-cin`, and what free config fix comes before buying bigger SKUs?
9. **SQL Serverless Trade-off:** Why did the `2s` Worker outbox poller burn the monthly free vCore-seconds in `2.31 days`, and what happens on cold resume (`Error 40613`)?
10. **Rebuild Gaps:** What 8 state items did our Bicep + pipeline rebuild test expose (e.g., `sqlAllowedIps`, SQL `EXTERNAL USER`s, CI UAMI placement in `rg-azurelab-ndvd-shared-cin`, seed data)?
