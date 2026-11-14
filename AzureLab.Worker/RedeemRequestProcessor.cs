using Azure.Messaging.ServiceBus;
using AzureLab.Messaging;
using AzureLab.Worker;
using Microsoft.Extensions.Options;

public class RedeemRequestMessageHandler(
    RedeemHandler redeemHandler,
    IOptionsMonitor<ChaosOptions> chaos,
    IHostEnvironment env,
    ILogger<RedeemRequestMessageHandler> logger) : IMessageHandler
{
    private static long _totalChaosAttempts;

    public async Task<bool> HandleAsync(ServiceBusReceivedMessage message, CancellationToken ct)
    {
        if (message.Subject != MessageTypes.RedeemRequested) return false;

        var cmd = message.Body.ToObjectFromJson<RedeemRequested>();

        if (cmd is null || cmd.ReedemId == Guid.Empty || cmd.OfferId == Guid.Empty)
            throw new PermanentMessageException("MalformedPayload", "The message payload is malformed.");

        await ApplyChaosAsync(cmd.ReedemId, message.DeliveryCount, ct);

        var outcome = await redeemHandler.HandleAsync(cmd, message.CorrelationId, ct);
        logger.LogInformation(
            "Redeem request {RedeemId} for offer {OfferId} processed with outcome {Outcome}, Delivery Count: {DeliveryCount}.",
            cmd.ReedemId, cmd.OfferId, outcome, message.DeliveryCount);
        return true; // Duplicate / AlreadyFinal / Complete / Reject (never abandon or DeadLetter)
    }

    private async Task ApplyChaosAsync(Guid redeemId, int deliveryCount, CancellationToken ct)
    {
        if (!env.IsDevelopment()) return; // never in production
        var c = chaos.CurrentValue;
        switch (c.Mode)
        {
            case "ThrowStorm":
                // Anti-pattern: inner retry loop (4 attempts) stacked inside Service Bus DeliveryCount (3 deliveries) = 12x amplification!
                for (var inner = 1; inner <= 4; inner++)
                {
                    var total = Interlocked.Increment(ref _totalChaosAttempts);
                    logger.LogWarning(
                        "Chaos ThrowStorm (amplified): RedeemId={RedeemId} DeliveryCount={DeliveryCount}/3 InnerAttempt={Inner}/4 TotalChaosAttempts={Total}",
                        redeemId, deliveryCount, inner, total);
                    if (inner < 4) await Task.Delay(50, ct);
                }
                throw new TimeoutException("Chaos ThrowStorm: inner retry loop exhausted (4x3=12 attempts per message)");

            case "Throw":
                // Fixed: fail fast inside handler; let ONE layer (Service Bus PeekLock / MaxDeliveryCount=3) manage delivery retries.
                var singleTotal = Interlocked.Increment(ref _totalChaosAttempts);
                logger.LogWarning(
                    "Chaos Throw (single-layer): RedeemId={RedeemId} DeliveryCount={DeliveryCount}/3 TotalChaosAttempts={Total}",
                    redeemId, deliveryCount, singleTotal);
                throw new TimeoutException("Chaos: simulated transient failure");
        }
    }
}
