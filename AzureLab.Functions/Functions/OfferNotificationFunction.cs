using AzureLab.Data;
using Azure.Messaging.ServiceBus;
using AzureLab_Functions.Messaging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace AzureLab_Functions.Functions
{
    public sealed class OfferNotificationFunction(AppDbContext db, 
        ILogger<OfferNotificationFunction> logger)
    {
        private const string ConsumerName = "fn-offer-notifications";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        
        [Function("OfferNotificationFunction")]
        public async Task Run([ServiceBusTrigger("offer-notifications", Connection = "ServiceBusConnection")]
            ServiceBusReceivedMessage message, 
            ServiceBusMessageActions actions,
            CancellationToken ct)
        {

            using var scope = logger.BeginScope(new Dictionary<string, object>
            {
                ["MessageId"] = message.MessageId,
                ["DeliveryCount"] = message.DeliveryCount,
                ["Subject"] = message.Subject
            });

            OfferNotification? notification;

            try
            {
                notification = message.Body.ToObjectFromJson<OfferNotification>(Json);
            }
            catch (JsonException ex)
            {
                await actions.DeadLetterMessageAsync(message,  
                    null, "DeserializationFailed", ex.Message, ct);
                logger.LogWarning("Dead lettered: invalid json");
                return;
            }

            if (notification is null || notification.OfferId == Guid.Empty || notification.EventId == Guid.Empty)
            {
                await actions.DeadLetterMessageAsync(message,
                    null, "ValidationFailed", "Notification is null or has invalid IDs", ct);
                logger.LogWarning("Dead lettered: Validation failed. MessageId: {MessageId}", message.MessageId);
                return;
            }

            var logEntry = new OfferNotificationLog
            {
                NotificationId = notification.NotificationId,
                OfferId = notification.OfferId,
                EventId = notification.EventId,
                NotificationType = notification.NotificationType,
                CreatedUtc = DateTime.UtcNow
            };

            var processedMessage = new ProcessedMessage
            {
                MessageId = message.MessageId,
                Consumer = ConsumerName,
                ProcessedUtc = DateTime.UtcNow
            };

            // Execution Strategy does not support user initiated transactions
            //await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.OfferNotificationLogs.Add(logEntry);
            db.ProcessedMessages.Add(processedMessage);

            try
            {
                await db.SaveChangesAsync();
                //await transaction.CommitAsync(ct);
            }
            catch(DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601  })
            {
                logger.LogInformation("Duplicate {MessageId}, outcome=Duplicate", message.MessageId);
                await actions.CompleteMessageAsync(message, ct);
                return;
            }

            await actions.CompleteMessageAsync(message, ct);
            logger.LogInformation("Processed {MessageId}, outcome=Success", message.MessageId);
        }
    }
}
