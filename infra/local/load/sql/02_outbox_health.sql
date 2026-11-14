-- INVARIANT 2: outbox drains, lag is bounded, nothing silently parked at MaxAttempts.
DECLARE @Since datetimeoffset = '2000-01-01';   -- baseline cut-off
DECLARE @MaxAttempts int = 5;   -- keep equal to Outbox:MaxAttempts

-- Lag (ms) = ProcessedUtc - CreatedUtc, per destination
SELECT DISTINCT
       Destination,
       COUNT(*) OVER (PARTITION BY Destination) AS sent_rows,
       PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY DATEDIFF_BIG(MILLISECOND, CreatedUtc, ProcessedUtc)) OVER (PARTITION BY Destination) AS lag_p50_ms,
       PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY DATEDIFF_BIG(MILLISECOND, CreatedUtc, ProcessedUtc)) OVER (PARTITION BY Destination) AS lag_p95_ms,
       MAX(DATEDIFF_BIG(MILLISECOND, CreatedUtc, ProcessedUtc)) OVER (PARTITION BY Destination) AS lag_max_ms
FROM dbo.OutboxMessages
WHERE CreatedUtc >= @Since AND ProcessedUtc IS NOT NULL;

-- Backlog: unsent but still retryable
SELECT Destination, COUNT(*) AS unsent_count, MIN(CreatedUtc) AS oldest_unsent
FROM dbo.OutboxMessages
WHERE CreatedUtc >= @Since AND ProcessedUtc IS NULL AND RetryCount < @MaxAttempts
GROUP BY Destination;

-- Parked: reached MaxAttempts, dispatcher ignores them forever (defect 2: no alert). PASS = 0 rows.
SELECT Id, Destination, Subject, RetryCount, Error, CreatedUtc
FROM dbo.OutboxMessages
WHERE CreatedUtc >= @Since AND ProcessedUtc IS NULL AND RetryCount >= @MaxAttempts;


