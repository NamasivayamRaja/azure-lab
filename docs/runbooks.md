# Alert Runbooks (5-Line Standard)

## 1. `alert-sb-dlq-*` (Service Bus Dead-Letter Queue > 0)
1. **Trigger:** `DeadletteredMessages > 0` (`Maximum`, `PT5M`) on `redeem-requests`, `ledger`, or `notification`.
2. **Triage:** Run `DlqTool peek <entity>` to inspect `DeadLetterReason`, `DeadLetterErrorDescription`, and `CorrelationId`.
3. **Trace:** Run KQL Query #1 (Trace by `CorrelationId`) and Query #3 (Consumer Settle Outcomes) in `docs/queries/kql-pack.kql`.
4. **Mitigate:** If transient/fixed bug, deploy fix or `DlqTool resubmit <entity> <seq>`; if poison/invalid contract, `DlqTool discard` and notify owner.
5. **Verify:** Confirm `DeadletteredMessages == 0` and alert auto-resolves.

## 2. `alert-sb-active-stuck-redeem` (Active Messages Stuck > 15m)
1. **Trigger:** `ActiveMessages > 0` (`Minimum`, `PT15M`) on `redeem-requests`.
2. **Triage:** Check `app-azurelab-worker-dev-cin-5kaayi` status and `GET /health/ready`.
3. **Trace:** Run KQL Query #2 (Failed Requests & Dependencies) and Query #3 for `Abandon` / `MessageLockLost` loops.
4. **Mitigate:** Start Worker if stopped; if SQL or Service Bus RBAC/network is broken, redeploy Bicep (`infra.yml`).
5. **Verify:** Confirm `ActiveMessages` drains to `0` and `OutboxMessages.ProcessedUtc` advances.

## 3. `alert-api-http-5xx` (API HTTP 5xx > 2 in 5m)
1. **Trigger:** `Http5xx > 2` (`Total`, `PT5M`) on `app-azurelab-api-dev-cin-5kaayi`.
2. **Triage:** Run KQL Query #2 (Failed Requests & Dependencies) and Query #6 (`StorageBlobLogs` 403s).
3. **Trace:** Open failing `OperationId` in App Insights Transaction Search to pinpoint SQL, Blob, or code exception.
4. **Mitigate:** If RBAC drift (403), redeploy Bicep; if bad release, swap back to previous slot or roll back commit.
5. **Verify:** Confirm `Http5xx == 0` and `GET /health/ready` returns `200`.

## 4. `alert-api-latency` (API Average Response Time > 2s)
1. **Trigger:** `HttpResponseTime > 2s` (`Average`, `PT5M`) on `app-azurelab-api-dev-cin-5kaayi`.
2. **Triage:** Run KQL Query #5 (Slow SQL Dependencies > 500ms) and check `sqldb-azurelab-dev-cin` status/DTU.
3. **Trace:** Separate serverless SQL auto-resume (~30–60s cold start) from sustained slow queries or downstream timeouts.
4. **Mitigate:** If sustained, check missing indexes, connection pool exhaustion, or scale SQL/App Service Plan.
5. **Verify:** Confirm `p95` SQL duration < 500 ms and `HttpResponseTime < 2s`.

## 5. `alert-api-health-availability` (`/health/live` Web Test Failed)
1. **Trigger:** `webtest-api-health-live` fails (`AppAvailabilityResults | where Success == false`).
2. **Triage:** Check App Service state (`Running`?), recent slot swap, and TLS certificate validity.
3. **Trace:** Query `AppServiceHTTPLogs` and `AppRequests` for `/health/live` status codes and timeouts.
4. **Mitigate:** Restart API if wedged, or roll back bad deployment/config change.
5. **Verify:** Confirm `/health/live` returns `200` from both test locations.

## 6. `alert-outbox-lag` (Outbox Dispatch Lag > 60s or Worker Stopped)
1. **Trigger:** `Redeem accepted` has no matching `Outbox message sent` within 60s (`PT15M` window).
2. **Triage:** Check `app-azurelab-worker-dev-cin-5kaayi` running state and `GET /health/ready`.
3. **Trace:** Run KQL Query #4 (Outbox Lag & `OutboxPoisonMessage` alerts) and check `OutboxMessages.RetryCount` in SQL.
4. **Mitigate:** Start Worker if stopped; if Service Bus `403`/network failure, redeploy Bicep.
5. **Verify:** Confirm backlog drains (`LagSeconds < 5`) and `Outbox message sent` resumes.

## 7. `alert-function-failures` (Functions Execution Failure)
1. **Trigger:** `AppRequests (Success == false)` or `AppExceptions` > 0 for `func-azurelab-dev-cin-5kaayi` (`PT5M`).
2. **Triage:** Run KQL Query #2 filtered to `func-azurelab-dev-cin-5kaayi` and Query #6 (`StorageBlobLogs` 403s).
3. **Trace:** Inspect exception stack trace (`SqlException`, `RequestFailedException`, or malformed blob/queue payload).
4. **Mitigate:** Restore RBAC via Bicep if `403`; if poison blob/message, inspect quarantine/DLQ.
5. **Verify:** Confirm subsequent Function invocations succeed (`Success == true`).
