# ADR 002: CI/CD Pipeline, Slot Strategy, and Database Migration Ordering

- **Status:** Accepted (Cloud Week 1, Days 3, 5 & 7)
- **Context:** We deploy three .NET 10 workloads (`API`, `Worker`, `Functions`) and an Azure SQL schema from a public GitHub repository (`NamasivayamRaja/azure-lab`). We need zero-downtime HTTP releases, safe schema evolution, protection against duplicate background processing during swaps, and fast feedback without paying for idle duplicate plans.

## Decision
1. **Two Workflows with Path Filtering:**
   - [infra.yml](../../.github/workflows/infra.yml): Runs `az bicep build` + `what-if` on PRs and `az deployment group create` on merge to `main` (live Azure jobs gated by `vars.AZURE_DEPLOY_ENABLED`).
   - [apps.yml](../../.github/workflows/apps.yml): Uses `dorny/paths-filter@v3` so `API`, `Worker`, `Functions`, and `EF Migrations` build and deploy only when their code or shared dependencies change (live deploy gated by `vars.AZURE_DEPLOY_ENABLED`).
2. **Self-Contained `efbundle` Before App Deploys:**
   - Build a Linux self-contained EF Core migration bundle (`efbundle`) in CI.
   - Open a short-lived SQL firewall rule for the GitHub runner IP, execute `efbundle` using the deploy UAMI (`Authentication=Active Directory Default`), and remove the firewall rule in an `if: always()` step before any app code swaps.
3. **Asymmetric Slot Strategy Across Compute Types:**
   - **API (`staging` slot):** Deploy to `staging`, warm up `/health/live`, swap to production, then **stop `staging`** to free memory on the shared `P0v3` plan.
   - **Functions (`staging` slot with triggers disabled):** Deploy to `staging` with sticky app settings `AzureWebJobs.<Function>.Disabled = true` so the staging slot never competes for Service Bus or Blob triggers before swap; stop `staging` post-swap.
   - **Worker (slotless direct deploy):** No staging slot. Deploying to a running staging slot would double-poll the SQL Outbox and compete on Service Bus queues; safety is guaranteed instead by `SIGTERM` graceful shutdown, 30s Outbox leases, Service Bus peek-lock expiry (`PT30S`), and idempotent consumers.
4. **Package Deployment (`WEBSITE_RUN_FROM_PACKAGE = 1`) on `P0v3`:**
   - Mount the zip directly as a read-only filesystem instead of extracting thousands of files over SMB (`OneDeploy`), which timed out (`504`) at `4m49s` on `S1`.

## Alternatives Considered
- **Staging Slot for Worker:** Rejected. A live Worker staging slot immediately starts background `IHostedService` loops unless custom slot-aware gating is built, and Worker has no inbound HTTP traffic requiring warm-up routing.
- **Running Migrations on App Startup (`Database.Migrate()`):** Rejected. Causes race conditions across multiple instances during scale-out or slot warm-up and requires granting `db_ddladmin` to runtime app identities (violating ADR 001).
- **Keeping Staging Slots Running 24/7:** Rejected on a shared single-instance `P0v3` plan because idle staging processes consume RAM needed by active workloads.

## Consequences
- **Positive:** HTTP swaps complete with zero dropped requests; runtime identities have zero DDL permissions; `WEBSITE_RUN_FROM_PACKAGE=1` + `P0v3` eliminated `504` deploy timeouts; `include-hidden-files: true` in artifact upload preserved `.azurefunctions/` metadata.
- **Negative / Operational Trade-offs:**
  - **Backward-Compatible Migrations Required:** Because `efbundle` runs before the slot swap, all schema changes must follow the expand-and-contract pattern so `v1` code remains functional while `v2` warms up.
  - **Stopped Staging Slot Returns `403` at Rest:** Operators must remember `staging` is intentionally stopped between releases.
