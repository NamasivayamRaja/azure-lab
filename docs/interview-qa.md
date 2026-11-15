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
