using AzureLab.Data;
using System.Text.Json;

namespace AzureLab.Messaging
{
    public static class Outbox
    {
        public static OutboxMessage Create<T>(Guid messageId
         , string destination, string subject, T payload,
         string correlationId, IDictionary<string, string>? properties = null)
        {
            return new OutboxMessage
            {
                Id = messageId,
                Destination = destination,
                Subject = subject,
                Payload = JsonSerializer.Serialize(payload),
                CreatedUtc = DateTimeOffset.UtcNow,
                CorrelationId = correlationId,
                Properties = properties != null ? JsonSerializer.Serialize(properties) : null,
                RetryCount = 0
            };
        }
    }
}
