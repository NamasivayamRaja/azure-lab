using Azure.Storage.Blobs;
using AzureLab.Data;
using AzureLab.Messaging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureLab.Functions.Functions;

public sealed class EventDocumentFunction(
    AppDbContext db, BlobServiceClient blobService, DocumentEventPublisher publisher,  ILogger<EventDocumentFunction> logger)
{
    private const long MaxBytes = 10 * 1024 * 1024; // 10 MB
    private const string QuarantineContainer = "event-docs-quarantine";

    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
    };

    [Function(nameof(EventDocumentFunction))]
    //[ServiceBusOutput("document-processed", Connection = "ServiceBusConnection")]
    public async Task Run(
        [BlobTrigger("event-docs/{name}", Connection = "AzureWebJobsStorage")] BlobClient blob,
        string name,
        CancellationToken ct)
    {
        // Metadata only: never download the whole blob just to check it.
        var props = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
        var etag = props.ETag.ToString();

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["Blob"] = name, ["ETag"] = etag });

        var reason = Validate(name, props.ContentType, props.ContentLength);

        var doc = new EventDocument
        {
            BlobName = name,
            ETag = etag,
            EventId = TryGetEventId(name),      
            ContentType = props.ContentType,
            SizeBytes = props.ContentLength,
            Status = reason is null ? "Accepted" : "Rejected",
            RejectedReason = reason,
            ProcessedUtc = DateTime.UtcNow
        };

        db.EventDocuments.Add(doc);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            db.ChangeTracker.Clear();
            var existing = await db.EventDocuments.AsNoTracking().SingleAsync(d => d.BlobName == name && d.ETag == etag);

            if (existing.Status == "Rejected")
                await QuarantineAsync(blob, name, ct);
            else
                await publisher.PublishAsync(ToEvent(doc), ct);

            logger.LogInformation("Duplicate blob version, outcome=Duplicate");
            return;
        }

        if (reason is not null)
        {
            // Business rejection: record it, move it aside, DO NOT throw.
            await QuarantineAsync(blob, name, ct);
            logger.LogWarning("Rejected: {Reason}, outcome=Rejected", reason);
           return;
        }

        logger.LogInformation("Accepted {size} bytes, outcome=Accepted", props.ContentLength);
        //Step 5: publish DocumentProcessed here.
        //return new DocumentProcessed($"{name}|{etag}", name, doc.EventId, props.ContentType, props.ContentLength, doc.ProcessedUtc);
        await publisher.PublishAsync(ToEvent(doc), ct);
    }

    private static DocumentProcessed ToEvent(EventDocument doc) => 
        new($"{doc.BlobName}|{doc.ETag}", doc.BlobName, doc.EventId, doc.ContentType ?? "", doc.SizeBytes, doc.ProcessedUtc);

    private static string? Validate(string name, string? contentType, long size)
    {
        if (size == 0) return "EmptyFile";
        if (size > MaxBytes) return $"TooLarge ({size} bytes > {MaxBytes})";

        var ext = Path.GetExtension(name);
        if (!Allowed.TryGetValue(ext, out var expectedType)) return $"ExtensionNotAllowed ({ext})";
        if (!string.Equals(contentType, expectedType, StringComparison.OrdinalIgnoreCase))
            return $"ContentTypeMismatch ({contentType})";

        return null;
    }

    private static Guid? TryGetEventId(string name)
    {
        var first = name.Split('/')[0];
        return Guid.TryParse(first, out var id) ? id : null;
    }

    private async Task QuarantineAsync(BlobClient source, string name, CancellationToken ct)
    {
        var target = blobService.GetBlobContainerClient(QuarantineContainer);
        await target.CreateIfNotExistsAsync(cancellationToken: ct);

        var copy = await target.GetBlobClient(name).StartCopyFromUriAsync(source.Uri, cancellationToken: ct);
        await copy.WaitForCompletionAsync(ct);
        await source.DeleteIfExistsAsync(cancellationToken: ct);
    }

    private async Task TagAsync(BlobClient blobClient, string status, string reasonCode, Guid? eventId, CancellationToken ct)
    {
        var existing = (await blobClient.GetTagsAsync(cancellationToken: ct)).Value.Tags;
        var tags = new Dictionary<string, string>(existing)
        {
            ["status"] = status,
            ["reason"] = reasonCode,
            ["processedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };

        if (eventId is not null) tags["eventId"] = eventId.Value.ToString();

        await blobClient.SetTagsAsync(tags, cancellationToken: ct);
    }
}
