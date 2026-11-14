using Azure.Messaging.ServiceBus;
using AzureLab.Messaging;

namespace AzureLab.Worker
{
    public class LedgerMessageHandler(ILogger<LedgerMessageHandler> logger) : IMessageHandler
    {
        public Task<bool> HandleAsync(ServiceBusReceivedMessage message, CancellationToken ct)
        {
            switch (message.Subject)
            {
                case MessageTypes.OfferRedeemed:
                    var payload = message.Body.ToObjectFromJson<OfferRedeemed>();
                    logger.LogInformation("[Ledger] Received offer redeemed message. MessageId={MessageId}, DeliveryCount={DeliveryCount}", message.MessageId, message.DeliveryCount);
                    return Task.FromResult(true);
                case MessageTypes.OfferRedeemRejected:
                    var no = message.Body.ToObjectFromJson<OfferRedeemRejected>();
                    logger.LogInformation("[Ledger] Received offer redeem rejected message. MessageId={MessageId}, DeliveryCount={DeliveryCount}", message.MessageId, message.DeliveryCount);
                    return Task.FromResult(true);
                default:
                    logger.LogWarning("[Ledger] Received unknown message. MessageId={MessageId}, Subject={Subject}", message.MessageId, message.Subject);
                    return Task.FromResult(false);
            }
        }
    }
}
