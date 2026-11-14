using Azure.Messaging.ServiceBus;
using AzureLab.Messaging;

namespace AzureLab.Worker
{
    public class NotificationMessageHandler(ILogger<NotificationMessageHandler> logger) : IMessageHandler
    {
        public Task<bool> HandleAsync(ServiceBusReceivedMessage message, CancellationToken ct)
        {
            if(message.Subject != MessageTypes.OfferRedeemed) return Task.FromResult(false);

            var payload = message.Body.ToObjectFromJson<OfferRedeemed>();
            logger.LogInformation("[Notification] Received offer redeemed message. MessageId={MessageId}, DeliveryCount={DeliveryCount}", message.MessageId, message.DeliveryCount);
            return Task.FromResult(true);

        }
    }

}