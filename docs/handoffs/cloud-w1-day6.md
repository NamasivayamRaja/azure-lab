================================================================================
HANDOFF BLOCK FOR DAY 7 — LOAD, AUTOSCALE, FAILURES & CAPACITY COMPLETE
================================================================================

1. TEST ENVIRONMENT & NETWORK BASELINE
- Client location: Chennai (`curl` baseline to `centralindia` / Pune): DNS `28–30 ms`, TCP RTT `26–31 ms`, cold TLS `100–155 ms`, warm single-connection `/health/live` RTT `p50 = 131.5 ms`.
- k6 keep-alive floor vs server: `k6` median (`41.4 ms`) minus `AppRequests.DurationMs` `p50` (`19.0 ms`) = **`22.4 ms`** network overhead.
- Auth adaptation: Added optional `-e AUTH_TOKEN=...` header support to [redeem-ramp.js](file:///d:/2026/AzureLabs/infra/local/load/k6/redeem-ramp.js), [oversell-race.js](file:///d:/2026/AzureLabs/infra/local/load/k6/oversell-race.js), and [storm-burst.js](file:///d:/2026/AzureLabs/infra/local/load/k6/storm-burst.js) for Entra `ApiAccess` JWTs.

2. LOCAL BASELINE VS AZURE (`1x P0v3` + `GP_S_Gen5_1` + Service Bus Standard)
| Metric | Local Post-Fix | Azure `1x P0v3` Ramp (`1,220` tx) | Azure `1x P0v3` Burst (`200` tx) | Azure `1->2` Spike (`9,187` tx) |
|---|---|---|---|---|
| **Throughput** | `18.0 rps` | `21.84 rps` | `101.32 rps` | `36.62 rps` |
| **Client HTTP `202` (`p50` / `p95` / `max`)** | `9.2` / `71.1` / `289.2 ms` | `41.4` / `93.4` / `2,813 ms` | `323.1` / `836.5` / `1,173 ms` | `205.7` / `1,200` / `15,330 ms` |
| **Server `AppRequests` (`p50` / `p95` / `p99`)** | N/A | `19.0` / `599.0` / `2,694.8 ms` | Included in ramp window | `20.2` / `511.4` / `901.2 ms` (SQL deps) |
| **Error Rate (`http_req_failed`)** | `0.00%` | `0.00%` | `0.00%` | `0.00%` |
| **Outbox Lag (`p50` / `p95` / `max`)** | `p95 = 2,800 ms` | `redeem`: `1.37s` / `2.26s` / `2.52s`<br>`events`: `1.28s` / `2.17s` / `2.40s` | Drained (`0` unsent) | `redeem`: `67.0s` / `98.1s` / `102.3s`<br>`events`: `1.60s` / `88.6s` / `102.2s` |
| **Service Bus `ActiveMessages` Peak** | Emulator | `259` (`Throttled=0`) | `0` (`Throttled=0`) | **`3,276`** |
| **SQL `cpu_percent` / `sessions_count`** | Local Docker | Max `17%` / `33` sessions | Same window | Max **`98.0%`** (avg `92%`) / **`79`** sessions |
| **App Service Plan CPU / Memory** | Local process | Max `55%` / `58–69%` | Same window | Scaled `1 -> 2` instances |
| **SQL Invariants (`01`–`04`)** | `PASS` | `0` stuck, `0` dedup violations | `0` oversell (`Remaining=50`) | `5,000` Completed, `2,084` Failed (`>60s`), `2,103` Rejected, `Remaining=0`, `0` stuck |

**Top 3 Differences Explained by Evidence:**
1. **Queue backlog (`259` -> `3,276`) & `2,084` reconciler timeouts:** [appservice.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/appservice.bicep#L72-L82) caps `Consumers__Redeem__MaxConcurrentCalls = 1` and `Outbox__BatchSize = 20` (vs `16` and `50` locally), causing `redeem-requests` outbox lag `p95` to reach `98.1 s` (> `RedeemReconciler` `60s` SLA).
2. **Client vs Server latency floor & tail:** `k6` adds `~22.4 ms` RTT (`41.4 - 19.0 ms`); at `101 rps` burst, 50 concurrent TLS handshakes (`p95 = 122.8 ms`) + shared `1x P0v3` (`69%` RAM) pushed server `p99` to `2.69 s`.
3. **SQL `GP_S_Gen5_1` saturation at `36.6 rps`:** SQL `cpu_percent` hit `98.0%` (`79` sessions), pushing API `p95` to `1.20 s` (`max 15.33 s`) and causing `6` transient SQL failures out of `11,904` Worker SQL calls.

3. AUTOSCALE RULES & MEASURED LAG (`asp-azurelab-dev-cin`)
- **Coupling:** `API`, `Worker`, and `Functions` all share `asp-azurelab-dev-cin` (`P0v3`). Scaling `1 -> 2` clones all 3 hosts together (doubling SQL session pools).
- **Rules (`autoscale-asp-azurelab-dev-cin`, `min=1, max=2, default=1`):**
  - Scale Out (`+1`, `PT5M` window, `PT5M` cooldown, OR): `CpuPercentage > 60%` OR `ActiveMessages > 50`.
  - Scale In (`-1`, `PT10M` window, `PT10M` cooldown, AND): `CpuPercentage < 25%` AND `ActiveMessages < 10`.
- **Measured Lag:**
  - `t0 = 05:31:37Z` (spike start) -> `t1 = 05:34:52.8Z` (autoscale initiated, **`3m 16s`**) -> `t2 = 05:34:55.2Z` (`plan_instances=2`) -> `t3 = 05:38:40.3Z` (Worker #2 first message processed, **`+3m 45s` warm-up**).
  - **Total reactive lag: `7m 03s`**.

4. SCALE-IN & MID-BATCH TERMINATION SAFETY
- **Multi-instance proof:** Under `2` Workers, `AppTraces` showed `471`/`86` `Completed`, `132`/`139` `Rejected`, `114`/`27` `Duplicate` (absorbed from `RedeemReconciler`), and `1` `AlreadyFinal` — with **`0` oversells** (`Remaining = 0`) and **`0` stuck/orphan rows**.
- **Mid-batch recycle (`Step 6.4`):** Fired `926` requests (`36.68 rps`), observed **`476` active messages** in `redeem-requests`, and ran `az webapp restart` on `app-azurelab-worker-dev-cin-5kaayi` mid-flight. Within 60s, all `476` messages drained cleanly (`activeMessageCount = 0`, `deadLetterMessageCount = 1` pre-existing).
- **Functions Dedicated vs Consumption:** On `asp-azurelab-dev-cin` (Dedicated), `host.json` `maxConcurrentCalls = 8` is a strict per-plan-instance cap (`N × 8`) with no queue-based scale controller; on Consumption, the scale controller uses `maxConcurrentCalls` as a scaling target hint.

5. FAILURE MATRIX (DEV)
| Failure | Symptom | Detected By | Time to Detect | Recovery / MTTR | Data Impact |
|---|---|---|---|---|---|
| **SQL Serverless auto-pause (`GP_S_Gen5_1`)** | Does **not** pause while Worker polls every `2s` (`2.31 days` free-limit burn). When paused, first connection throws **`Error 40613`** (~30–60s cold resume). | `AppDependencies` (`Success==false`) / `alert-api-latency` (`>2s` avg) | `1–5m` | Auto-recovers via `Connection Timeout=60` + EF `EnableRetryOnFailure` | `0` lost/corrupt rows |
| **SQL CPU Saturation (`36.6 rps` spike)** | SQL `cpu_percent = 98%`, `79` sessions, API `p95 = 1.20 s` (`max 15.33 s`), `6/11,904` Worker SQL failures | `AppDependencies` SQL `p95 = 511 ms`, SQL metrics | `< 1m` | Auto-recovered on load drop; `RedeemReconciler` healed `2,084` stale rows (`>60s`) | `0` oversell, `0` stuck |
| **Service Bus Standard Throttling** | `50009 ServerBusyException` above `1,000 credits/sec` namespace cap | `ThrottledRequests` metric (observed `0` at ~220 credits/s) | `1m` grain | SDK exponential retry + Outbox retry (`MaxAttempts = 5`) | `0` lost messages |
| **Worker Recycle Mid-Batch (`476` active)** | `SIGTERM` -> 30s -> `SIGKILL`; in-flight locks expire after `PT30S` | `AppTraces` / `alert-sb-active-stuck-redeem` (if stalled >15m) | `< 60s` drain | Auto-drained all `476` messages in `< 60s`; `0` new DLQs | `0` duplicates / `0` lost |

6. CAPACITY & COST SUMMARY
- **Cost per 1,000 redeems (at `36.6 rps` / `131.8k redeems/hr` saturation):** **~\$0.002–\$0.004** marginal (`P0v3` ~\$0.0834/hr + SB \$10/mo base + \$0.80/1M ops + SQL Serverless vCore-sec). Fixed idle baseline dominates low-traffic cost.
- **Spoken Capacity Plan:** *"At single-instance dev sizing (`1x P0v3`, `GP_S_Gen5_1` Serverless SQL, `Service Bus Standard`), the API ingests `36.6 rps` sustained (`101 rps` burst) with zero HTTP failures, costing ~\$0.003 per 1,000 redeems at saturation. The two immediate bottlenecks are Worker consumer concurrency (`MaxConcurrentCalls=1` in Bicep, which queued 3,276 messages and triggered `RedeemReconciler` at `60s`) and single-vCore SQL saturation (`98%` CPU, `79` sessions). Because App Service Linux autoscale takes ~7 minutes end-to-end (~3.5m evaluation + ~3.5m cold-start), we rely on Service Bus to absorb short bursts, raise `MaxConcurrentCalls` to 8–16 and `Outbox__BatchSize` to 50 first, and in production move to provisioned `GP_Gen5_2` SQL and separate `P1v3` plans for API and Worker."*

7. PROPOSED CHANGES BACKLOG (NOT YET APPLIED)
1. **[appservice.bicep](file:///d:/2026/AzureLabs/infra/bicep/modules/appservice.bicep#L72-L82):** Raise `Consumers__Redeem__MaxConcurrentCalls` and `Consumers__Ledger__MaxConcurrentCalls` (`1 -> 8`), and `Outbox__BatchSize` (`20 -> 50`).
2. **[ServiceBusConsumer.cs](file:///d:/2026/AzureLabs/AzureLab.Messaging/ServiceBusConsumer.cs#L70-L105) & `Program.cs`:** Pass `CancellationToken.None` to `CompleteMessageAsync` on already-committed work; catch `OperationCanceledException` during shutdown to `AbandonMessageAsync` immediately instead of waiting `PT30S`; set `HostOptions.ShutdownTimeout = 25s`.
3. **Production Plan Split:** Separate `asp-api` (HTTP scaling + slots) from `asp-worker` (queue-depth scaling, no slots) and use provisioned `GP_Gen5_2` SQL (`prod.bicepparam`).
================================================================================
