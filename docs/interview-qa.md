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
