-- INVARIANT 1: every RedeemTransaction becomes final (Completed/Rejected/Failed) within @MaxAgeSeconds.
-- Status enum: 0 Pending, 1 Processing, 2 Completed, 3 Failed, 4 Rejected.
-- PASS = stuck_count is 0 once the system is idle (queue drained). Run it AFTER the load ends.
DECLARE @Since datetimeoffset = '2000-01-01';   -- baseline cut-off (run-invariants.ps1 -SinceUtc replaces this)
DECLARE @MaxAgeSeconds int = 60;

SELECT Status, COUNT(*) AS cnt
FROM dbo.RedeemTransactions WHERE RequestedAtUtc >= @Since
GROUP BY Status
ORDER BY Status;

SELECT COUNT(*) AS stuck_count,
       MIN(RequestedAtUtc) AS oldest_stuck
FROM dbo.RedeemTransactions
WHERE RequestedAtUtc >= @Since AND Status IN (0, 1)
  AND RequestedAtUtc < DATEADD(SECOND, -@MaxAgeSeconds, SYSDATETIMEOFFSET());

-- Which ones (first 20) - Processing = crashed mid-handle, Pending = never consumed / outbox not sent
SELECT TOP (20) Id, Status, RequestedAtUtc, CorrelationId
FROM dbo.RedeemTransactions
WHERE RequestedAtUtc >= @Since AND Status IN (0, 1)
  AND RequestedAtUtc < DATEADD(SECOND, -@MaxAgeSeconds, SYSDATETIMEOFFSET())
ORDER BY RequestedAtUtc;


