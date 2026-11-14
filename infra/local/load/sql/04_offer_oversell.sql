-- INVARIANT 4: Offer capacity is never oversold (completed_count <= Capacity AND Remaining = Capacity - completed_count >= 0).
DECLARE @Since datetimeoffset = '2000-01-01';   -- baseline cut-off (run-invariants.ps1 -SinceUtc replaces this)

SELECT i.OfferId,
       i.Capacity,
       i.Remaining,
       ISNULL(tx.completed_cnt, 0) AS completed_since,
       ISNULL(tx.rejected_cnt, 0)  AS rejected_since,
       CASE
           WHEN ISNULL(tx.completed_cnt, 0) > i.Capacity OR i.Remaining < 0 THEN 'FAIL_OVERSOLD'
           ELSE 'PASS'
       END AS invariant_status
FROM dbo.OfferInventories i
OUTER APPLY (
    SELECT SUM(CASE WHEN t.Status = 2 THEN 1 ELSE 0 END) AS completed_cnt,
           SUM(CASE WHEN t.Status = 4 THEN 1 ELSE 0 END) AS rejected_cnt
    FROM dbo.RedeemTransactions t
    WHERE t.OfferId = i.OfferId AND t.RequestedAtUtc >= @Since
) tx;
