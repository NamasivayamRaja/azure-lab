using AzureLab.Data;
using AzureLab.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureLab.Worker;

public sealed class ReconcilerOptions
{
    public const string SectionName = "Reconciler";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(60);
    public int BatchSize { get; set; } = 50;
}

public sealed class RedeemReconciler(
    IServiceScopeFactory scopeFactory,
    IOptions<ReconcilerOptions> options,
    IOptions<ServiceBusOptions> sbOptions,
    ILogger<RedeemReconciler> logger) : BackgroundService
{
    private static readonly Guid DefaultEventId = Guid.Parse("F878DD09-415E-48EA-A4E6-9091A29FCE80");
    private readonly ReconcilerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "RedeemReconciler started. PollInterval={PollInterval}, StaleAfter={StaleAfter}, BatchSize={BatchSize}",
            _options.PollInterval, _options.StaleAfter, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileStaleTransactionsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "RedeemReconciler loop error");
            }

            await Task.Delay(_options.PollInterval, stoppingToken);
        }
    }

    private async Task ReconcileStaleTransactionsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateTimeOffset.UtcNow.Subtract(_options.StaleAfter);

        var staleRows = await db.RedeemTransactions
            .AsNoTracking()
            .Where(x => (x.Status == RedeemTransactionStatus.Pending || x.Status == RedeemTransactionStatus.Processing)
                        && x.RequestedAtUtc < cutoff)
            .OrderBy(x => x.RequestedAtUtc)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (staleRows.Count == 0) return;

        var strategy = db.Database.CreateExecutionStrategy();

        foreach (var stale in staleRows)
        {
            await strategy.ExecuteAsync(async innerCt =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(innerCt);

                // Atomic conditional transition: if 2 Worker instances race on the same stale row,
                // only the one that flips Status from Pending/Processing -> Failed (updated == 1) emits the compensating event.
                var updated = await db.RedeemTransactions
                    .Where(x => x.Id == stale.Id
                                && (x.Status == RedeemTransactionStatus.Pending || x.Status == RedeemTransactionStatus.Processing))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RedeemTransactionStatus.Failed), innerCt);

                if (updated == 0)
                {
                    await tx.RollbackAsync(innerCt);
                    return;
                }

                var msgId = stale.Id.ToString();
                var hasInbox = await db.ProcessedMessages
                    .AnyAsync(p => p.Consumer == RedeemHandler.Consumer && p.MessageId == msgId, innerCt);

                if (!hasInbox)
                {
                    db.ProcessedMessages.Add(new ProcessedMessage
                    {
                        Consumer = RedeemHandler.Consumer,
                        MessageId = msgId,
                        ProcessedUtc = DateTimeOffset.UtcNow
                    });
                }

                db.OutboxMessages.Add(Outbox.Create(
                    messageId: Guid.NewGuid(),
                    destination: sbOptions.Value.OfferTopic,
                    subject: MessageTypes.OfferRedeemRejected,
                    payload: new OfferRedeemRejected(
                        stale.Id,
                        stale.OfferId,
                        DefaultEventId,
                        stale.UserId,
                        DateTimeOffset.UtcNow),
                    correlationId: stale.CorrelationId ?? stale.Id.ToString()));

                await db.SaveChangesAsync(innerCt);
                await tx.CommitAsync(innerCt);
                db.ChangeTracker.Clear();

                logger.LogWarning(
                    "Reconciled stale RedeemTransaction {RedeemId} (was {PreviousStatus}, RequestedAtUtc={RequestedAtUtc}) -> Failed + OfferRedeemRejected",
                    stale.Id, stale.Status, stale.RequestedAtUtc);
            }, ct);
        }
    }
}
