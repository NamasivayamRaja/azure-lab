-- INVARIANT 3: idempotent consumer. Exactly ONE inbox row (and one final transaction) per redeem request,
-- however many times Service Bus delivered it.
-- "Deliveries" are not stored in SQL: count them from Worker logs (see README) and compare:
--   deliveries (log lines "Redeem request ... processed with outcome")  -  inbox rows  =  duplicates absorbed
DECLARE @Since datetimeoffset = '2000-01-01';   -- baseline cut-off (run-invariants.ps1 -SinceUtc replaces this)

SELECT COUNT(*) AS redeem_outbox_rows_sent
FROM dbo.OutboxMessages
WHERE Destination = 'redeem-requests' AND ProcessedUtc IS NOT NULL AND CreatedUtc >= @Since;

SELECT COUNT(*) AS redeem_transactions FROM dbo.RedeemTransactions WHERE RequestedAtUtc >= @Since;

SELECT COUNT(*) AS redeem_inbox_rows
FROM dbo.ProcessedMessages p
WHERE p.Consumer = 'redeem'
  AND EXISTS (SELECT 1 FROM dbo.RedeemTransactions t WHERE CONVERT(nvarchar(200), t.Id) = p.MessageId AND t.RequestedAtUtc >= @Since);

-- Must be 0: a final transaction with no inbox row means the handler skipped idempotency
SELECT COUNT(*) AS tx_without_inbox
FROM dbo.RedeemTransactions t
WHERE t.RequestedAtUtc >= @Since
  AND t.Status IN (2, 3, 4)
  AND NOT EXISTS (SELECT 1 FROM dbo.ProcessedMessages p WHERE p.Consumer = 'redeem' AND p.MessageId = CONVERT(nvarchar(200), t.Id));

-- Must be 0: inbox row without a transaction (poison / orphan)
SELECT COUNT(*) AS inbox_without_tx
FROM dbo.ProcessedMessages p
WHERE p.Consumer = 'redeem' AND p.ProcessedUtc >= @Since
  AND NOT EXISTS (SELECT 1 FROM dbo.RedeemTransactions t WHERE CONVERT(nvarchar(200), t.Id) = p.MessageId);

-- Must be 0: each final (Completed/Failed/Rejected) transaction produces exactly ONE downstream event (offer-events) in the outbox
SELECT COUNT(*) AS tx_with_wrong_event_count
FROM (
    SELECT t.Id, COUNT(o.Id) AS ev
    FROM dbo.RedeemTransactions t
    LEFT JOIN dbo.OutboxMessages o
           ON o.Destination = 'offer-events'
           AND ISJSON(Payload) = 1
          AND JSON_VALUE(o.Payload, '$.ReedemId') = CONVERT(nvarchar(50), t.Id)
    WHERE t.RequestedAtUtc >= @Since AND t.Status IN (2, 3, 4)
    GROUP BY t.Id
    HAVING COUNT(o.Id) <> 1
) x;
