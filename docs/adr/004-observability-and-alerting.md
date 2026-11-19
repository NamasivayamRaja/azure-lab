# ADR 004: End-to-End Distributed Tracing, Log-Based Alerting, and Cost/Privacy Guardrails

- **Status:** Accepted (Cloud Week 1, Day 5)
- **Context:** In an asynchronous architecture (`API -> SQL Outbox -> Worker -> Service Bus -> Worker/Functions -> SQL`), standard HTTP request tracing breaks at the database outbox boundary. Additionally, naive metric alerts fail when a background worker stops emitting metrics, and unrestricted Log Analytics ingestion or PII logging creates cost and compliance risks.

## Decision
1. **OpenTelemetry + W3C Trace Propagation Across the SQL Outbox (Zero Schema Change):**
   - Enable `Azure.Monitor.OpenTelemetry.AspNetCore` (`1.6.0`) and `Azure.Experimental.EnableActivitySource = true` with `IncludeScopes = true` across API and Worker.
   - Persist `Activity.Current` W3C `traceparent` and `tracestate` inside `OutboxMessage.Properties` JSON during `Outbox.Create`.
   - In `OutboxDispatcher`, restore the parent context via custom `ActivitySource("AzureLab.Outbox")` (`ActivityKind.Producer`) before calling `ServiceBusSender.SendMessageAsync`, so the SDK injects `Diagnostic-Id` and links the entire async chain under a single `OperationId`.
2. **Symptom-Based Metric & Log Alerts ([alerts.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/alerts.bicep)):**
   - **Service Bus DLQ (`Sev 1`):** Metric alert on `DeadletteredMessages > 0` (`timeAggregation: 'Maximum'`, `PT1M` frequency) for `redeem-requests` and `offer-events` subscriptions.
   - **Outbox Lag (`Sev 2`):** `scheduledQueryRules` KQL alert detecting `Redeem accepted` traces without a matching `Outbox message sent` on the same `OperationId` within 60s (`PT5M` window). Chosen over a Worker-emitted custom metric because a crashed/stopped Worker emits zero metrics ("silent death").
   - **API Availability, 5xx & Latency:** Metric alerts on `Http5xx > 5` and `HttpResponseTime > 2s`, plus availability check on `/health/live`.
3. **Cost & Privacy Hardening:**
   - Cap `log-azurelab-dev-cin` (`PerGB2018`) at `1 GB/day` with `30-day` retention and subscription budget `budget-azurelab-week1` (₹3,000/mo at 50/80/100% actual + 100% forecast).
   - Strip document filenames (`EventDocumentStore`, `PostgresDocumentVectorStore`) and client IP addresses (`HealthFunction`) from structured logs.

## Alternatives Considered
- **Adding `TraceParent` Column to `OutboxMessages`:** Rejected. Storing `traceparent` in the existing `Properties` JSON dictionary required zero EF migrations while preserving full W3C trace continuity.
- **Worker-Emitted Outbox Queue-Depth Metric:** Rejected for alerting. When `app-azurelab-worker` was stopped during Day 5 chaos testing, metric emission ceased; only the KQL join across API and Worker traces detected the stalled outbox (`TTD = 6m 18s`).

## Consequences
- **Positive:** One `OperationId` query in App Insights reconstructs the entire lifecycle (`HTTP 202 -> SQL Outbox -> Service Bus -> RedeemHandler -> Ledger/Notification`). Proven TTD in chaos tests: `5m 21s` (DLQ poison), `6m 18s` (Worker stopped), `3m 26s` (Storage RBAC 403).
- **Negative / Operational Trade-offs:**
  - Log Analytics ingestion adds ~2–5 minutes of indexing latency to `scheduledQueryRules` alerts compared to ~1–2 minutes for platform metric alerts.
