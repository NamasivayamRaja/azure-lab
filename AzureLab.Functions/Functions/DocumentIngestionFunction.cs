using Azure.Messaging.ServiceBus;
using AzureLab.Messaging;
using AzureLab.Rag;
using AzureLab.Rag.Ingestion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AzureLab.Functions.Functions;

public sealed class DocumentIngestionFunction(
    DocumentIngestionService ingestion,
    IOptions<AiOptions> aiOptions,
    ILogger<DocumentIngestionFunction> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Function(nameof(DocumentIngestionFunction))]
    public async Task Run(
        [ServiceBusTrigger("document-processed", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken ct)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["MessageId"] = message.MessageId,
            ["CorrelationId"] = message.CorrelationId,
            ["DeliveryCount"] = message.DeliveryCount
        });

        // A2: If AI is disabled (Ai:Enabled=false) and the function was not disabled via AzureWebJobs.DocumentIngestionFunction.Disabled,
        // dead-letter explicitly with reason "AiDisabled" so the event is preserved in the DLQ for replay once AI is enabled
        // (instead of completing and losing the document or letting 1h TTL expire silently when DeadLetteringOnMessageExpiration=false).
        if (!aiOptions.Value.Enabled)
        {
            await actions.DeadLetterMessageAsync(
                message,
                null,
                "AiDisabled",
                "Ai:Enabled is false; preserving message in DLQ until AI ingestion is enabled.",
                ct);
            logger.LogWarning("Dead lettered: Ai:Enabled is false");
            return;
        }

        DocumentProcessed? evt;
        try
        {
            evt = message.Body.ToObjectFromJson<DocumentProcessed>(Json);
        }
        catch (JsonException ex)
        {
            await actions.DeadLetterMessageAsync(message, null, "DeserializationFailed", Truncate(ex.Message), ct);
            logger.LogWarning("Dead lettered: invalid json");
            return;
        }

        if (evt is null || string.IsNullOrWhiteSpace(evt.BlobName) || string.IsNullOrWhiteSpace(evt.DocumentKey)
            || !evt.DocumentKey.StartsWith(evt.BlobName + "|", StringComparison.Ordinal)
            || evt.DocumentKey.Length == evt.BlobName.Length + 1)
        {
            await actions.DeadLetterMessageAsync(message, null, "ValidationFailed",
                "Missing BlobName/DocumentKey, or DocumentKey is not '{BlobName}|{ETag}'", ct);
            logger.LogWarning("Dead lettered: validation failed");
            return;
        }

        var etag = evt.DocumentKey[(evt.BlobName.Length + 1)..];
        using var docScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["Blob"] = evt.BlobName,
            ["EventId"] = evt.EventId,
            ["DocumentKey"] = evt.DocumentKey
        });

        IngestionResult result;
        try
        {
            result = await ingestion.IngestAsync(
                new IngestionRequest(evt.DocumentKey, evt.BlobName, etag, evt.EventId, evt.ProccessedUtc), ct);
        }
        catch (PermanentIngestionException ex)
        {
            RagTelemetry.IngestOutcomes.Add(1, RagTelemetry.Tag("outcome", "Permanent:" + ex.Reason));
            await actions.DeadLetterMessageAsync(message, null, ex.Reason, Truncate(ex.Message), ct);
            logger.LogError("Dead lettered: {Reason}", ex.Reason);
            return;
        }
        // Anything else (Ollama down, Postgres down, timeouts) escapes: host abandons, DLQ after MaxDeliveryCount.

        logger.LogInformation("outcome={Outcome} chunks={Chunks} detail={Detail}",
            result.Outcome, result.ChunkCount, result.Detail);
        RagTelemetry.IngestOutcomes.Add(1, RagTelemetry.Tag("outcome", result.Outcome.ToString()));
        await actions.CompleteMessageAsync(message, ct);
    }

    private static string Truncate(string s) => s.Length <= 1024 ? s : s[..1024];
}
