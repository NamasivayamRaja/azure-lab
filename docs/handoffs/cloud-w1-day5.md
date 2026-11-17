================================================================================
HANDOFF BLOCK FOR DAY 6 — OBSERVABILITY, ALERTS & PRODUCTION HARDENING COMPLETE
================================================================================

1. TELEMETRY MAP PER HOST
| Host | AppRoleName | SDK / Exporter | Signals Emitted | Destination |
|---|---|---|---|---|
| API (`AzureLabs.Api`) | `app-azurelab-api-dev-cin-5kaayi` | `Azure.Monitor.OpenTelemetry.AspNetCore` `1.6.0` (`UseAzureMonitor` + `IncludeScopes=true`) | `AppRequests`, `AppDependencies` (SQL, HTTP, SB), `AppTraces`, `AppExceptions`, `RagTelemetry` | `appi-azurelab-dev-cin` -> `log-azurelab-dev-cin` |
| Worker (`AzureLab.Worker`) | `app-azurelab-worker-dev-cin-5kaayi` | `Azure.Monitor.OpenTelemetry.AspNetCore` `1.6.0` (`AzureLab.Outbox` + `Azure.Messaging.ServiceBus.*` + `IncludeScopes=true`) | `AppRequests` (`ServiceBusProcessor.ProcessMessage`), `AppDependencies` (`Outbox.Dispatch`, SQL, SB), `AppTraces` | `appi-azurelab-dev-cin` -> `log-azurelab-dev-cin` |
| Functions (`AzureLab.Functions`) | `func-azurelab-dev-cin-5kaayi` | `Microsoft.ApplicationInsights.WorkerService` `2.23.0` + `ConfigureFunctionsApplicationInsights` | `AppRequests` (100% kept via `excludedTypes: "Request"`), `AppDependencies`, `AppTraces` (`Information+`) | `appi-azurelab-dev-cin` -> `log-azurelab-dev-cin` |

2. END-TO-END TRACE PROPAGATION (OUTBOX + SERVICE BUS)
- `Outbox.Create` (`AzureLab.Messaging/OutboxExtensions.cs`): Captures `Activity.Current?.Id` (`traceparent`) and `TraceStateString` (`tracestate`) into `OutboxMessage.Properties` (no DB schema change).
- `OutboxDispatcher` (`AzureLab.Worker/OutboxDispatcher.cs`): Restores `parentContext` via `ActivityContext.TryParse`, starts `AzureLab.Outbox` (`ActivityKind.Producer`), strips raw `traceparent`/`tracestate` before send so `ServiceBusSender` injects live `Diagnostic-Id`.
- `ServiceBusProcessor` + `ServiceBusConsumer`: Extracts `Diagnostic-Id` automatically to parent consumer spans (`ServiceBusProcessor.ProcessMessage`) and scopes `CorrelationId` + `DeliveryCount`.
- Proven end-to-end `OperationId`: `3b942879202aa1948e8c6b6447634ea8` across API (`POST /offers/{id}/redeem`) -> `Outbox.Dispatch` -> `Redeem` -> `Outbox.Dispatch` -> `Ledger`/`Notification`.

3. KQL PACK (`docs/queries/kql-pack.kql`)
1. **Trace by `CorrelationId`**: Resolves `OperationId` and prints full chronological timeline across `AppRequests`, `AppDependencies`, `AppTraces`, `AppExceptions`.
2. **Failed requests & dependencies**: `Success == false` + `AppExceptions` across all hosts.
3. **Consumer settle outcomes + `DeliveryCount`**: Classifies `Complete`, `Abandon`, `DeadLetter` from Worker logs.
4. **Outbox lag**: Joins API `Redeem accepted` vs Worker `Outbox message sent` on `CorrelationId` (`LagSeconds`), plus `OutboxPoisonMessage` alerts.
5. **Slow SQL dependencies**: `DependencyType == "SQL"` > 500 ms and `p95`/`max` latency.
6. **Day 2 & Day 4 security queries**: `StorageBlobLogs` `403`s, API `401`/`403`s, `AzureActivity` RBAC changes.

4. ALERT TABLE (`infra/bicep/modules/alerts.bicep` & `docs/runbooks.md`)
| Alert Name | Signal | Threshold / Window | Action Group | Runbook Summary |
|---|---|---|---|---|
| `alert-sb-dlq-redeem-requests` | `DeadletteredMessages` | `Maximum > 0` (`PT5M`, eval `PT1M`) | `ag-azurelab-ops` | `DlqTool peek`, inspect `DeadLetterReason` + KQL #1/#3, fix & resubmit/discard |
| `alert-sb-dlq-offer-events-ledger` | `DeadletteredMessages` | `Maximum > 0` (`PT5M`, eval `PT1M`) | `ag-azurelab-ops` | `DlqTool peek offer-events/Subscriptions/ledger`, triage & resubmit/discard |
| `alert-sb-dlq-offer-events-notification` | `DeadletteredMessages` | `Maximum > 0` (`PT5M`, eval `PT1M`) | `ag-azurelab-ops` | `DlqTool peek offer-events/Subscriptions/notification`, triage & resubmit/discard |
| `alert-sb-active-stuck-redeem` | `ActiveMessages` | `Minimum > 0` (`PT15M`, eval `PT5M`) | `ag-azurelab-ops` | Check Worker `/health/ready`, `Abandon`/`MessageLockLost` in KQL #3 |
| `alert-api-http-5xx` | `Http5xx` | `Total > 2` (`PT5M`, eval `PT1M`) | `ag-azurelab-ops` | KQL #2 + `StorageBlobLogs` `403`, redeploy Bicep or rollback slot |
| `alert-api-latency` | `HttpResponseTime` | `Average > 2s` (`PT5M`, eval `PT1M`) | `ag-azurelab-ops` | KQL #5 Slow SQL, separate serverless resume from sustained bottleneck |
| `alert-api-health-availability` | `webtest-api-health-live` (`/health/live`) | `AppAvailabilityResults Success == false` (`PT5M`) | `ag-azurelab-ops` | Check App Service state, slot swap, TLS cert, `AppServiceHTTPLogs` |
| `alert-outbox-lag` | Log query (`Redeem accepted` vs `Outbox message sent`) | Unsent > 60s (`PT15M`, eval `PT5M`) | `ag-azurelab-ops` | Check Worker running, `OutboxDispatcher` logs, `OutboxMessages.RetryCount` |
| `alert-function-failures` | Log query (`AppRequests`/`AppExceptions`) | Count > 0 (`PT5M`, eval `PT5M`) | `ag-azurelab-ops` | KQL #2 on `func-azurelab-dev-cin-5kaayi`, check Storage/SQL RBAC |

5. BREAK & DETECT RESULTS (DEV)
| Experiment | Fault Injected | Alert / Signal | Time to Detect | Resolution |
|---|---|---|---|---|
| 6.1 Poison message | Malformed JSON `"not-json{"` on `redeem-requests` | `alert-sb-dlq-redeem-requests` | **5m 21s** | `DlqTool purge` + `az role assignment delete` |
| 6.2 Stop Worker | Stopped `app-azurelab-worker-dev-cin-5kaayi` + `POST /redeem` (`202`) | `alert-outbox-lag` | **6m 18s** | `az webapp start` -> outbox drained |
| 6.3 SQL pause | Checked `sqldb-azurelab-dev-cin` (already `Online`; `99–124 ms` SQL) | KQL #5 (`AppDependencies`) | N/A (not paused) | Verified `PT5M` avg masks single cold-start |
| 6.4 Role removed | Deleted `Storage Blob Data Contributor` on container + 3x upload (`500`/`403`) | `alert-api-http-5xx` | **3m 26s** | Redeployed Bicep (`infra.yml`) -> drift fixed |

6. COST, PRIVACY & CI/CD
- Log Analytics capped at `dailyQuotaGb: 1`, `retentionInDays: 30`.
- Sanitized 3 log leaks: removed `{BlobName}` (`EventDocumentStore.cs`), `{Blob}` (`PostgresDocumentVectorStore.cs`), `{RemoteIpAddress}` (`HealthFunction.cs`).
- Updated `.github/workflows/apps.yml` with `dorny/paths-filter@v3` for selective app + dependency deploys.
================================================================================
