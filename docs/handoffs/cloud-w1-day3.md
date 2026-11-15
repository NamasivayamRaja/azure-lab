================================================================================
HANDOFF BLOCK FOR DAY 4 — OBSERVABILITY, ALERTS & PRODUCTION HARDENING
================================================================================

1. REPOSITORY, BRANCHING & PROTECTIONS
- GitHub Repo:         https://github.com/NamasivayamRaja/azure-lab (public, GitHub Free)
- Default Branch:      main (trunk-based: short-lived feature branches -> PR -> main)
- Branch Ruleset:      Active on main — PR required (0 approvals, solo contributor),
                       block force pushes, restrict deletions, required status check: build-and-whatif.
- Environments:        dev  (no required reviewers)
                       prod (required reviewer: NamasivayamRaja)
- Repo Variables:      AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID,
                       AZURELAB_OWNER=ndvd, AZURELAB_SQL_ADMIN_LOGIN, AZURELAB_SQL_ADMIN_OID
                       (zero secrets stored in GitHub)

2. WORKFLOWS
- .github/workflows/infra.yml
  * PR (infra/bicep/**): az bicep build + build-params + az deployment group what-if -> Job Summary.
  * Push to main: az deployment group create in environment: dev.
  * Permissions: id-token: write, contents: read.
- .github/workflows/apps.yml
  * Path-filtered to app/shared projects (AzureLabs.Api/**, AzureLab.Worker/**, AzureLab.Functions/**,
    AzureLab.Data/**, AzureLab.Messaging/**, AzureLab.Rag/**, apps.yml) + workflow_dispatch.
  * Build & publish: API, Worker, Functions (net10.0) + self-contained EF bundle (efbundle).
  * Upload artifact with include-hidden-files: true (preserves .azurefunctions/ for dotnet-isolated).
  * Deploy (main, environment: dev):
    1) Temporary SQL firewall rule for runner IP -> ./publish/efbundle (Entra OIDC) -> delete rule (always).
    2) API: start staging -> OneDeploy -> /health smoke test -> swap staging->prod -> stop staging.
    3) Functions: start staging -> OneDeploy -> /api/health smoke test -> swap staging->prod -> stop staging.
    4) Worker: direct OneDeploy (graceful SIGTERM shutdown + peek-lock/outbox lease safety).
    5) Production smoke tests (/health, /api/health).

3. DEPLOY IDENTITY & FEDERATED CREDENTIALS
- Identity:            id-azurelab-github-dev-cin (User-Assigned Managed Identity in rg-azurelab-ndvd-dev-cin)
- Principal ID:        1d8a9ab8-84b9-4969-872b-ca9eb3633a86
- Issuer / Audience:   https://token.actions.githubusercontent.com / api://AzureADTokenExchange

| Credential Name | Exact Subject (`sub`) | Purpose |
|---|---|---|
| gh-pr-whatif | `repo:NamasivayamRaja@10979213/azure-lab@1457712472:pull_request` | PR Bicep `what-if` (no environment) |
| gh-env-dev | `repo:NamasivayamRaja@10979213/azure-lab@1457712472:environment:dev` | Deploy infra, migrations, and apps to `dev` |
| gh-env-prod | `repo:NamasivayamRaja@10979213/azure-lab@1457712472:environment:prod` | Reviewer-gated `prod` deployment pattern |

4. ROLES TABLE (NEW / UPDATED IN DAY 3)
| Identity | Scope | Roles |
|---|---|---|
| `id-azurelab-github-dev-cin` | `rg-azurelab-ndvd-dev-cin` | `Contributor`, `Role Based Access Control Administrator` |
| `id-azurelab-github-dev-cin` | `sqldb-azurelab-dev-cin` | `EXTERNAL_USER`: `db_ddladmin`, `db_datareader`, `db_datawriter` |
| `func-azurelab-dev-cin-5kaayi/slots/staging` (`bb561a3b...`) | `stazurelabdev5kaayi` | `Storage Blob Data Contributor`, `Storage Queue Data Contributor` |

5. INFRA & SLOT SETTINGS (UPDATED IN DAY 3)
- App Service Plan:    Upgraded dev.bicepparam from S1 (1.75 GB, ~$2.28/day) to P0v3 (4 GB, ~$2.04/day).
- Run-From-Package:    WEBSITE_RUN_FROM_PACKAGE=1 enabled on API, Worker, and Functions.
- Functions Slot:      Added staging slot to functionapp.bicep (SystemAssigned MI).

| App | Sticky (`slotConfigNames`) | Non-Sticky (Swaps with Code) |
|---|---|---|
| API (`app-azurelab-api-dev-cin-5kaayi`) | `ASPNETCORE_ENVIRONMENT`, `WEBSITE_SWAP_WARMUP_PING_PATH` (`/health`), `WEBSITE_SWAP_WARMUP_PING_STATUSES` (`200`) | `ConnectionStrings__Default`, `ServiceBus__*`, `BlobStorage__*`, `Ai__Enabled`, `WEBSITE_RUN_FROM_PACKAGE` |
| Functions (`func-azurelab-dev-cin-5kaayi`) | `WEBSITE_SWAP_WARMUP_PING_PATH` (`/api/health`), `WEBSITE_SWAP_WARMUP_PING_STATUSES` (`200`), `AzureWebJobs.*.Disabled` (`true` in staging, `false` in prod) | `AzureWebJobsStorage__*`, `ServiceBusConnection__*`, `EventDocsStorage__*`, `ConnectionStrings__LabDb`, `WEBSITE_RUN_FROM_PACKAGE` |

6. ROLLBACK RUNBOOK (5 STEPS)
1. Confirm regression in production (API `/health` or `/health/ready`, Functions `/api/health`, or App Insights/5xx spike).
2. Start the `staging` slot if stopped (`az webapp start -g rg-azurelab-ndvd-dev-cin -n <app> --slot staging`) — it still holds the previous production build.
3. Swap `staging` back into `production` (`az webapp deployment slot swap -g rg-azurelab-ndvd-dev-cin -n <app> --slot staging --target-slot production`).
4. Verify production health endpoints (`/health`, `/health/ready`, `/api/health`) return `200 Healthy` and stop `staging` again.
5. For Worker or breaking schema issues: revert the bad commit via PR -> `main` (re-deploys Worker cleanly) and rely on Expand/Contract backward compatibility (never blindly drop columns).

7. RELEASE & NEGATIVE-TEST EVIDENCE (DAY 3)
- Real PR flow:        PRs #1–#6+ on `NamasivayamRaja/azure-lab` (branch -> PR `build-and-whatif` -> merge -> `deploy-dev`).
- Real swap & rollback: Exp 5 verified `az webapp deployment slot swap` forward and swap-back with `/health` returning `Healthy`.
- Negative tests:
  1. Warm-up failure (`WEBSITE_SWAP_WARMUP_PING_PATH=/nonexistent-warmup-test`): swap blocked (`Cannot complete the swap because the warm up request to path '/nonexistent-warmup-test' failed with status code '404'`), prod untouched.
  2. Bad app setting (`ConnectionStrings__Default="Server=invalid;"`): `/health/ready` returned `502 Bad Gateway` / unhealthy; restored via Bicep `dev.bicepparam`.
  3. Migration failure: observed `SqlException (18456): Login failed for user '<token-identified principal>'` before app deploy/swap; prod untouched.
  6. Wrong OIDC subject: observed `AADSTS700213: No matching federated identity record found for presented assertion subject 'repo:NamasivayamRaja@10979213/azure-lab@1457712472:pull_request'`.
  7. Missing deploy role (removed `Role Based Access Control Administrator`): OIDC login succeeded, `Deploy Bicep (Dev)` failed with `InvalidTemplateDeployment` (`does not have permission to perform action 'Microsoft.Authorization/roleAssignments/write'`). Restored immediately.
  8. Fork / untrusted PR: blocked from Environment variables/approvals and `:environment:*` OIDC subject (`AADSTS700213`).
================================================================================
