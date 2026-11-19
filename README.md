# AzureLab — Enterprise .NET 10 Cloud Architecture Lab

Production-grade reference workload built on **.NET 10**, **Azure App Service**, **Azure Functions (Isolated)**, **Azure Service Bus**, **Azure SQL Serverless**, **Azure Blob Storage**, and **Azure Monitor OpenTelemetry**, provisioned via **Bicep** and **GitHub Actions OIDC** with zero secrets, keys, or passwords.

## Architecture Diagram (Azure Deployment)

```mermaid
flowchart TB
    subgraph External["Client & CI/CD"]
        Client["Client / k6 Load Gen"]
        GH["GitHub Actions (OIDC)"]
    end

    subgraph SharedRG["rg-azurelab-ndvd-shared-cin"]
        UAMI["id-azurelab-github-dev-cin<br/>(User-Assigned MI)"]
    end

    subgraph Entra["Microsoft Entra ID"]
        AppReg["api-azurelab-dev<br/>(v2 JWT: scp=api.access / roles=Api.Service)"]
    end

    subgraph WorkloadRG["rg-azurelab-ndvd-dev2-cin (centralindia)"]
        subgraph Plan["asp-azurelab-dev-cin (Linux P0v3, Autoscale 1..2)"]
            API["app-azurelab-api<br/>(+ staging slot)"]
            Worker["app-azurelab-worker<br/>(OutboxDispatcher + Consumers + Reconciler)"]
            Func["func-azurelab<br/>(+ staging slot, Blob/SB Triggers)"]
        end

        subgraph Data["Data & Messaging Plane (Local Auth Disabled)"]
            SQL[("sqldb-azurelab-dev-cin<br/>(GP_S_Gen5_1 Serverless, Entra-only)")]
            SB["sb-azurelab-dev-cin<br/>(Standard, disableLocalAuth=true)<br/>Queues: redeem-requests, offer-notifications, document-processed<br/>Topic: offer-events (ledger, notification)"]
            Blob[("stazurelabdev<br/>(allowSharedKeyAccess=false)<br/>Containers: bulk-player-enrollment, event-docs, event-docs-quarantine")]
        end

        subgraph Obs["Observability & Guardrails"]
            AppI["appi-azurelab-dev-cin<br/>+ log-azurelab-dev-cin (1GB/day cap)"]
            Alerts["Metric & Log Alerts<br/>(DLQ, Outbox Lag, 5xx, Latency)"]
        end
    end

    Client -->|"1. Acquire JWT"| AppReg
    Client -->|"2. HTTPS Bearer JWT"| API
    GH -->|"Federated OIDC"| UAMI
    UAMI -->|"Bicep + efbundle + ZipDeploy"| WorkloadRG

    API -->|"Write RedeemTx + OutboxMessage (MI)"| SQL
    API -->|"User-Delegation SAS / Blob (MI)"| Blob
    Worker -->|"Poll Outbox / Atomic Decrement / Inbox Dedup (MI)"| SQL
    Worker <-->|"Publish & Consume AMQP (MI)"| SB
    Func <-->|"BlobTrigger + Quarantine + SB Queue (MI)"| Blob
    Func <-->|"Consume & Publish (MI)"| SB
    Func -->|"Idempotent Log (MI)"| SQL

    API -.->|"OTel W3C traceparent"| AppI
    Worker -.->|"OTel W3C traceparent"| AppI
    Func -.->|"OTel / Host Logs"| AppI
    AppI --> Alerts
```

## Quick Start

### 1. Local Development
```powershell
# 1. Start local dependencies (SQL Server, Azurite, Service Bus Emulator, Postgres pgvector)
docker compose -f infra/local/docker-compose.yml up -d

# 2. Configure Entra ID settings for API authentication (zero secrets in repo)
dotnet user-secrets set "AzureAd:TenantId" "<your-tenant-id>" --project AzureLabs.Api
dotnet user-secrets set "AzureAd:ClientId" "<api-app-client-id>" --project AzureLabs.Api

# 3. Apply EF Core migrations & run
dotnet ef database update --project AzureLab.Infrastructure --startup-project AzureLabs.Api
dotnet run --project AzureLabs.Api
dotnet run --project AzureLab.Worker
```

### 2. Azure Provisioning (Bicep)
```powershell
$env:AZURELAB_OWNER = 'ndvd'
$env:AZURE_TENANT_ID = '<tenant-id>'
$env:AZURELAB_API_CLIENT_ID = '<api-app-client-id>'
$env:ALERT_EMAIL = '<alert-email>'
$env:AZURELAB_SQL_ADMIN_LOGIN = '<admin-upn>'
$env:AZURELAB_SQL_ADMIN_OID = '<admin-object-id>'

az deployment group what-if -g rg-azurelab-ndvd-dev2-cin `
  -f infra/bicep/main.bicep -p infra/bicep/params/dev.bicepparam
```

### 3. Documentation & Evidence
- **Architecture Decision Records (ADRs):** [`docs/adr/`](file:///d:/2026/AzureLabs/docs/adr/)
- **Operational Runbooks:** [`docs/runbooks.md`](file:///d:/2026/AzureLabs/docs/runbooks.md)
- **Cloud Week 1 Engineering Log:** [`docs/cloud-week1-log.md`](file:///d:/2026/AzureLabs/docs/cloud-week1-log.md)
- **Daily Handoffs & Evidence:** [`docs/handoffs/`](file:///d:/2026/AzureLabs/docs/handoffs/)
- **Interview Q&A Bank:** [`docs/interview-qa.md`](file:///d:/2026/AzureLabs/docs/interview-qa.md)
