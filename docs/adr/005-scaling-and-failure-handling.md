# ADR 005: Asynchronous Scaling, Concurrency Control, and Failure Recovery

- **Status:** Accepted (Cloud Week 1, Day 6)
- **Context:** Flash-sale offer redemptions generate sharp traffic bursts (tested at `101.3 rps` burst and `9,187` transactions at `36.6 rps` sustained). Synchronous database locking under HTTP requests causes thread-pool starvation and timeouts, while multi-instance scale-out and mid-batch container restarts risk double-redemptions (overselling) or stuck `Pending` transactions.

## Decision
1. **Asynchronous Redeem Pipeline with Atomic Inventory Decrement:**
   - API validates the JWT, inserts `RedeemTransaction (Pending)` + `OutboxMessage` in one SQL transaction, and returns `202 Accepted` (`server p50 = 19.0 ms`).
   - `OutboxDispatcher` claims batches using `UPDLOCK, READPAST, ROWLOCK` with a 30s lease (`LockedUntilUtc`).
   - `RedeemHandler` executes a single atomic conditional update (`UPDATE OfferInventories SET Remaining = Remaining - 1 WHERE OfferId = @id AND Remaining > 0`) and records the message in `ProcessedMessages` (Inbox pattern) to guarantee zero oversells and idempotent redelivery.
2. **Self-Healing Reconciler (`RedeemReconciler`):**
   - Periodically scans for `RedeemTransactions` stuck in `Pending`/`Processing` past the `60s` SLA and republishes/fails them safely; downstream idempotent handlers absorb any resulting duplicate messages.
3. **Composite Autoscale on `asp-azurelab-dev-cin` (`min=1, max=2`):**
   - **Scale Out (`+1`, `PT5M` window/cooldown, OR):** Plan `CpuPercentage > 60%` OR Service Bus `ActiveMessages > 50` on `redeem-requests`.
   - **Scale In (`-1`, `PT10M` window/cooldown, AND):** `CpuPercentage < 25%` AND `ActiveMessages < 10` (prevents premature scale-in while the queue is draining at low CPU).
4. **Peek-Lock + At-Least-Once Recovery on Mid-Batch Termination:**
   - Service Bus `lockDuration = PT30S` with `maxDeliveryCount = 10`. If a Worker restarts mid-batch (`SIGTERM`/`SIGKILL`), uncommitted peek-locks expire after 30s and another instance drains the queue without data loss.

## Alternatives Considered
- **Synchronous Redeem in `POST /redeem`:** Rejected. Under the `9,187`-tx spike, single-vCore SQL hit `98%` CPU (`79` sessions); synchronous processing would turn queue backlog into HTTP `503`/`504` failures.
- **CPU-Only Autoscale:** Rejected. An I/O-bound Worker (`MaxConcurrentCalls = 1`) sits at low CPU while `ActiveMessages` climbs into the thousands (`3,276` observed).

## Consequences
- **Positive:** Proven `0.00%` HTTP error rate and `0` oversells (`5,000/5,000` inventory accuracy) across single-instance burst (`200` tx @ `101.3 rps`), 2-instance scale-out (`9,187` tx), and forced mid-batch Worker restart (`476` active messages drained in `< 60s` with `0` new DLQs).
- **Negative / Operational Trade-offs:**
  - **Reactive Autoscale Lag (`7m 03s`):** App Service Linux took `3m 16s` to trigger + `3m 45s` to warm up Worker #2. Short bursts (<5m) must be absorbed by Service Bus buffering and higher in-process concurrency (`MaxConcurrentCalls = 8`, `Outbox__BatchSize = 50`), not horizontal instance boot.
  - **Shared Plan Coupling:** Sharing `asp-azurelab-dev-cin` across API, Worker, and Functions clones all three hosts on scale-out, doubling SQL connection pools (`79` sessions). Production should split `asp-api` and `asp-worker`.
  - **SQL Serverless Auto-Pause Prevention:** Worker's `2s` outbox polling keeps `GP_S_Gen5_1` continuously awake, consuming the monthly free vCore-second limit in ~`2.31 days`.
