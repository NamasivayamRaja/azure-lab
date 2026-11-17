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
