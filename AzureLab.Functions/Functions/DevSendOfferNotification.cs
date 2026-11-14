#if DEBUG
using Azure.Messaging.ServiceBus;
using AzureLab_Functions.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text.Json;

namespace AzureLab_Functions.Functions
{
    public sealed class DevSendOfferNotification(ServiceBusClient client)
    {
        [Function("DevSendOfferNotification")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = "dev/offer-notifications")] HttpRequest req)
        {
            var mode = req.Query["mode"].ToString();
            var id = Guid.TryParse(req.Query["id"], out var parsedId) ? parsedId : Guid.NewGuid();

            var body = mode == "poison" ? BinaryData.FromString("{ not json")
                : BinaryData.FromObjectAsJson(
                    new OfferNotification(id, Guid.NewGuid(), Guid.NewGuid(), "OfferCreated", DateTime.UtcNow),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var message = new ServiceBusMessage(body) 
            { 
                MessageId = id.ToString(), 
                ContentType = "application/json", 
                Subject = "OfferCreated"
            };

            await using var sender = client.CreateSender("offer-notifications");
            await sender.SendMessageAsync(message);

            return new AcceptedResult(location: null, value: new {messageId = id, mode});
        }
    }
}
#endif
