using AzureLab.Data;
using AzureLab.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureLab.Worker;

public enum RedeemHandlerStatus
{
    Completed,
    Rejected,
    Duplicate,
    AlreadyFinal
}

public sealed class RedeemHandler(
    AppDbContext db,
    IOptions<ServiceBusOptions> sbOptions,
    IOptionsMonitor<ChaosOptions> chaos,
    IHostEnvironment env,
    ILogger<RedeemHandler> logger)
{
    public const string Consumer = "redeem";

    public async Task<RedeemHandlerStatus> HandleAsync(RedeemRequested cmd, string correlationId, CancellationToken cancellationToken)
    {
        var messageId = cmd.ReedemId.ToString();

        // Fast-path idempotency check before starting any work
        if (await db.ProcessedMessages.AsNoTracking()
                .AnyAsync(p => p.Consumer == Consumer && p.MessageId == messageId, cancellationToken))
        {
            return RedeemHandlerStatus.Duplicate;
        }

        var tx = await db.RedeemTransactions
            .FirstOrDefaultAsync(x => x.Id == cmd.ReedemId, cancellationToken)
            ?? throw new PermanentMessageException("RedeemTransactionNotFound", $"RedeemTransaction {cmd.ReedemId} not found");

        if (tx.Status is RedeemTransactionStatus.Completed or RedeemTransactionStatus.Rejected or RedeemTransactionStatus.Failed)
        {
            return RedeemHandlerStatus.AlreadyFinal;
        }

        if (env.IsDevelopment() && chaos.CurrentValue.Mode == "Slow")
        {
            logger.LogWarning(
                "Chaos: sleeping {Seconds}s mid-handler for tx {RedeemId} (kill Worker now to simulate mid-flight crash)",
                chaos.CurrentValue.SlowSeconds, cmd.ReedemId);
            await Task.Delay(TimeSpan.FromSeconds(chaos.CurrentValue.SlowSeconds), cancellationToken);
        }

        // Atomic commit: conditional inventory decrement + Inbox row + State transition + Outbox event
        // in ONE database transaction inside the EF execution strategy.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var dbTx = await db.Database.BeginTransactionAsync(ct);

            var currentTx = await db.RedeemTransactions
                .FirstOrDefaultAsync(x => x.Id == cmd.ReedemId, ct)
                ?? throw new PermanentMessageException("RedeemTransactionNotFound", $"RedeemTransaction {cmd.ReedemId} not found");

            if (currentTx.Status is RedeemTransactionStatus.Completed or RedeemTransactionStatus.Rejected or RedeemTransactionStatus.Failed)
            {
                await dbTx.RollbackAsync(ct);
                return RedeemHandlerStatus.AlreadyFinal;
            }

            var now = DateTimeOffset.UtcNow;
            bool isApproved;

            var hasInventoryRow = await db.OfferInventories.AsNoTracking()
                .AnyAsync(x => x.OfferId == cmd.OfferId, ct);

            if (hasInventoryRow)
            {
                if (cmd.UserId == "12345")
                {
                    // Atomic conditional UPDATE: SQL engine serializes row-level decrement with U->X lock,
                    // never oversells (Remaining > 0 guard), and never throws DbUpdateConcurrencyException.
                    var decremented = await db.OfferInventories
                        .Where(x => x.OfferId == cmd.OfferId && x.Remaining > 0)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.Remaining, x => x.Remaining - 1)
                            .SetProperty(x => x.UpdatedUtc, now), ct);

                    isApproved = decremented == 1;
                }
                else
                {
                    isApproved = false;
                }
            }
            else
            {
                // Fallback for unseeded offer IDs (e.g. Day 6 self-tests)
                isApproved = cmd.UserId == "12345";
            }

            currentTx.Status = isApproved ? RedeemTransactionStatus.Completed : RedeemTransactionStatus.Rejected;

            db.ProcessedMessages.Add(new ProcessedMessage
            {
                Consumer = Consumer,
                MessageId = messageId,
                ProcessedUtc = now
            });

            db.OutboxMessages.Add(isApproved
                ? Outbox.Create(
                    messageId: Guid.NewGuid(),
                    destination: sbOptions.Value.OfferTopic,
                    subject: MessageTypes.OfferRedeemed,
                    payload: new OfferRedeemed(cmd.ReedemId, cmd.OfferId, cmd.EventId, cmd.UserId, now),
                    correlationId: correlationId)
                : Outbox.Create(
                    messageId: Guid.NewGuid(),
                    destination: sbOptions.Value.OfferTopic,
                    subject: MessageTypes.OfferRedeemRejected,
                    payload: new OfferRedeemRejected(cmd.ReedemId, cmd.OfferId, cmd.EventId, cmd.UserId, now),
                    correlationId: correlationId));

            try
            {
                await db.SaveChangesAsync(ct);
                await dbTx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (DBErrors.IsUniqueViolation(ex))
            {
                await dbTx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return RedeemHandlerStatus.Duplicate;
            }

            return isApproved ? RedeemHandlerStatus.Completed : RedeemHandlerStatus.Rejected;
        }, cancellationToken);
    }
}
