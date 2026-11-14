using Azure.Messaging.ServiceBus;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AzureLab.Messaging
{
    public sealed class DocumentEventPublisher(ServiceBusClient client) : IAsyncDisposable
    {
        private readonly ServiceBusSender _sender = client.CreateSender("document-processed");
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public Task PublishAsync(DocumentProcessed evt, CancellationToken ct)
        {
            var msg = new ServiceBusMessage(BinaryData.FromObjectAsJson(evt, Json))
            {
                MessageId = StableId(evt.DocumentKey)
            };
            msg.ApplicationProperties["schemaVersion"] = 1;
            return _sender.SendMessageAsync(msg, ct);
        }
        private static string StableId(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

        public ValueTask DisposeAsync() => _sender.DisposeAsync();

    }

}
