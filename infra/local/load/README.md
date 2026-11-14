# Load / concurrency lab

```
infra/local/load/
  README.md
  run-invariants.ps1     # runs sql/*.sql + dlq-counts.ps1
  dlq-counts.ps1         # DLQ count per entity (via DlqTool peek)
  sql/
    01_stuck_transactions.sql   # every tx final within N s
    02_outbox_health.sql        # lag p50/p95, unsent, rows at MaxAttempts
    03_inbox_dedup.sql          # one inbox row / one event per tx
  k6/                           # (Step 2) HTTP load scripts
  results/                      # (Step 2+) saved k6 summaries
```

Counting Service Bus deliveries (for invariant 3), in the Worker console/log:
the line `Redeem request {RedeemId} ... processed with outcome {Outcome}` is written once per delivery.
`Duplicate` + `AlreadyFinal` outcomes = duplicates the idempotent consumer absorbed.

Emulator numbers are not representative: judge the invariants (all must be PASS), record p50/p95 for trends only.
