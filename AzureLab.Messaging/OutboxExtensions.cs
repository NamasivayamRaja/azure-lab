using AzureLab.Data;
using System.Diagnostics;
using System.Text.Json;

namespace AzureLab.Messaging
{
    public static class Outbox
    {
        public static OutboxMessage Create<T>(Guid messageId
         , string destination, string subject, T payload,
         string correlationId, IDictionary<string, string>? properties = null)
        {
            var merged = properties is not null
                ? new Dictionary<string, string>(properties)
                : new Dictionary<string, string>();

            var activity = Activity.Current;
            if (!string.IsNullOrWhiteSpace(activity?.Id))
            {
                merged.TryAdd("traceparent", activity.Id);
                if (!string.IsNullOrWhiteSpace(activity.TraceStateString))
                {
                    merged.TryAdd("tracestate", activity.TraceStateString);
                }
            }

            return new OutboxMessage
            {
                Id = messageId,
                Destination = destination,
                Subject = subject,
                Payload = JsonSerializer.Serialize(payload),
                CreatedUtc = DateTimeOffset.UtcNow,
                CorrelationId = correlationId,
                Properties = merged.Count > 0 ? JsonSerializer.Serialize(merged) : null,
                RetryCount = 0
            };
        }
    }
}
