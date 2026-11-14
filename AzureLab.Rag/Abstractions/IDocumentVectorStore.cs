namespace AzureLab.Rag.Abstractions;

public sealed record ChunkToStore(
    int ChunkIndex, string Content, string ContentHash, ReadOnlyMemory<float> Embedding);

public sealed record DocumentToStore(
    string DocumentKey,              // "{blobName}|{etag}", same as DocumentProcessed.DocumentKey
    string BlobName,
    Guid EventId,
    string EmbeddingModel,
    int EmbeddingDimensions,
    DateTimeOffset SourceVersionUtc, // DocumentProcessed.ProccessedUtc: orders versions of one blob
    IReadOnlyList<ChunkToStore> Chunks);

public sealed record RetrievedChunk(
    string DocumentKey, string BlobName, int ChunkIndex, string Content, double Similarity);

public enum ReplaceOutcome { Stored, Superseded }

public interface IDocumentVectorStore
{
    /// <summary>
    /// Atomic and idempotent. In ONE transaction (serialized per event+blob):
    /// if a NEWER version of the same blob already exists -> Superseded, nothing written.
    /// Otherwise upsert all chunks of DocumentKey, remove chunk indexes of that key no longer present,
    /// and remove chunks of OTHER versions of the same blob.
    /// Running it twice with the same input leaves the same state.
    /// </summary>
    Task<ReplaceOutcome> ReplaceDocumentAsync(DocumentToStore document, CancellationToken ct);

    /// <summary>
    /// Top-k nearest chunks. EventId is a mandatory filter, never optional.
    /// Similarity = cosine similarity, 1 = same direction, clamped at 0.
    /// </summary>
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        Guid eventId, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken ct);
}