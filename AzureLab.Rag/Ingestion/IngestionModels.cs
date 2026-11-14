namespace AzureLab.Rag.Ingestion;

public sealed record IngestionRequest(
    string DocumentKey, string BlobName, string ETag, Guid? EventId, DateTimeOffset SourceVersionUtc);

public enum IngestionOutcome
{
    Stored, Superseded,
    SkippedUnsupportedType, SkippedNoEventId, SkippedBlobChanged, SkippedNoText
}

public sealed record IngestionResult(IngestionOutcome Outcome, int ChunkCount = 0, string? Detail = null);

/// <summary>This message can never succeed: the host dead-letters it with Reason.</summary>
public sealed class PermanentIngestionException(string reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Reason { get; } = reason;
}

public interface IDocumentContentSource
{
    /// <summary>Returns the content of exactly this blob version, or null if it is gone or has changed.</summary>
    Task<Stream?> OpenAsync(string blobName, string etag, CancellationToken ct);
}

public interface IDocumentTextExtractor
{
    bool CanHandle(string extension);
    /// <exception cref="PermanentIngestionException">Document cannot be read.</exception>
    string ExtractText(Stream content);
}