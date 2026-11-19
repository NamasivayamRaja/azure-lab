# ADR 001: Zero-Secret Identity and Least-Privilege RBAC Model

- **Status:** Accepted (Cloud Week 1, Days 1–2 & Day 7)
- **Context:** The workload consists of three compute hosts (`API`, `Worker`, `Functions`) talking to Azure SQL Database, Azure Service Bus, and Azure Blob Storage, plus a GitHub Actions CI/CD pipeline. Storing connection strings, shared keys, SQL passwords, or client secrets in configuration or Key Vault introduces secret rotation toil, blast-radius risk, and credential-leak vectors in a public repository.

## Decision
1. **Disable Local Authentication Everywhere:**
   - Storage Account: `allowSharedKeyAccess = false`, `allowBlobPublicAccess = false`.
   - Service Bus Namespace: `disableLocalAuth = true`.
   - Azure SQL Server: `azureAdOnlyAuthentication = true` (Entra-only auth, TLS 1.2).
   - Key Vault: Not deployed (`deployKeyVault = false`) until a third-party secret with no Entra integration exists.
2. **System-Assigned Managed Identities per Compute Host (and Slot):**
   - Each app (`API`, `API/staging`, `Worker`, `Functions`, `Functions/staging`) uses its own System-Assigned Managed Identity.
3. **Resource-Scoped Least-Privilege Azure RBAC in Bicep ([roleassignments.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/roleassignments.bicep)):**
   - **API:** `Storage Blob Delegator` at Storage Account scope + `Storage Blob Data Contributor` scoped strictly to container `bulk-player-enrollment`. Zero Service Bus roles (API writes exclusively to SQL `OutboxMessages`).
   - **Worker:** `Azure Service Bus Data Sender` and `Receiver` scoped to individual queues (`redeem-requests`) and topic/subscriptions (`offer-events/ledger`, `offer-events/notification`). Zero Storage roles.
   - **Functions (+ staging):** `Storage Blob Data Contributor` and `Storage Queue Data Contributor` at Storage Account scope (required by the Functions host for leases/locks and `event-docs` quarantine copy/delete), plus queue-scoped Service Bus roles (`offer-notifications`, `document-processed`).
4. **Decoupled CI/CD Bootstrap Identity:**
   - Provision a User-Assigned Managed Identity (`id-azurelab-github-dev-cin`) in a dedicated `rg-azurelab-ndvd-shared-cin` resource group with GitHub OIDC federated credentials locked to immutable repository/owner IDs (`@10979213`, `@1457712472`).

## Alternatives Considered
- **Connection Strings / Shared Keys in Key Vault:** Rejected. Adds ~\$0.03/10k operations, requires secret rotation, and is redundant when all target Azure services natively support Entra ID data-plane RBAC.
- **Namespace/Account-Wide `Owner` or `Contributor` Data Roles:** Rejected. Violates least privilege; negative testing proved `Storage Blob Data Contributor` satisfies both Functions host locking and blob quarantine operations without needing `Storage Blob Data Owner`.
- **DefaultAzureCredential in Production:** Replaced/constrained in favor of explicit credential selection (`ManagedIdentityCredential` in Azure, `AzureCliCredential` locally) to avoid fallback chain latency and stray environment-variable hijacking.

## Consequences
- **Positive:** Zero secrets in git, GitHub Actions, or app settings. Compromising the API host cannot publish directly to Service Bus or read `event-docs-quarantine`. Tearing down the workload resource group does not destroy the CI/CD identity in `rg-azurelab-ndvd-shared-cin`.
- **Negative / Operational Trade-offs:**
  - **RBAC & SAS Caching Lag:** Control-plane role revocations take ~56s–200s+ to propagate to Storage/AMQP sessions, and user-delegation keys cached in memory (`2h`) require an app restart for immediate revocation.
  - **SQL Data-Plane Gap:** Bicep cannot execute `CREATE USER ... FROM EXTERNAL PROVIDER`; rebuilding a resource group mints new System-Assigned Managed Identities that require a post-deploy SQL grant step (bound by `OBJECT_ID` when display names collide).
