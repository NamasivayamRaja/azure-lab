using System.Security.Cryptography;
using System.Text;
using AzureLab.Rag.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureLab.Rag.Ingestion;

public sealed class DocumentIngestionService(
    IEnumerable<IDocumentTextExtractor> extractors,
    IDocumentContentSource source,
    ITextEmbedder embedder,
    IDocumentVectorStore store,
    IOptions<AiOptions> options,
    ILogger<DocumentIngestionService> logger)
{
    public async Task<IngestionResult> IngestAsync(IngestionRequest req, CancellationToken ct)
    {
        var o = options.Value.Ingestion;

        // 1) Cheap business decisions first: no download, no model call.
        var ext = Path.GetExtension(req.BlobName);
        var extractor = extractors.FirstOrDefault(e => e.CanHandle(ext));
        if (extractor is null)
            return new(IngestionOutcome.SkippedUnsupportedType, Detail: $"unsupported '{ext}' (image/OCR out of scope)");

        if (req.EventId is not { } eventId || eventId == Guid.Empty)
            return new(IngestionOutcome.SkippedNoEventId, Detail: "blob is not under an {eventId}/ path");

        // 2) Download exactly this version.
        await using var content = await source.OpenAsync(req.BlobName, req.ETag, ct);
        if (content is null)
            return new(IngestionOutcome.SkippedBlobChanged, Detail: "blob deleted or replaced by a newer version");

        // 3) Extract + chunk (deterministic: failures are permanent).
        var text = extractor.ExtractText(content);
        var pieces = TextChunker.Chunk(text, o.ChunkSizeChars, o.ChunkOverlapChars);
        if (pieces.Count == 0)
            return new(IngestionOutcome.SkippedNoText, Detail: "no extractable text (scanned document?)");
        if (pieces.Count > o.MaxChunksPerDocument)
            throw new PermanentIngestionException("TooManyChunks",
                $"{pieces.Count} chunks exceeds Ai:Ingestion:MaxChunksPerDocument={o.MaxChunksPerDocument}.");

        // 4) Embed in batches (transient failures escape -> host abandons -> DLQ after max deliveries).
        var chunks = new List<ChunkToStore>(pieces.Count);
        for (var i = 0; i < pieces.Count; i += o.EmbedBatchSize)
        {
            var batch = pieces.Skip(i).Take(o.EmbedBatchSize).ToList();
            using var embedSpan = RagTelemetry.Source.StartActivity("rag.ingest.embed_batch");
            embedSpan?.SetTag("rag.batch_size", batch.Count);
            var vectors = await embedder.EmbedDocumentsAsync(batch, ct);
            for (var j = 0; j < batch.Count; j++)
                chunks.Add(new ChunkToStore(i + j, batch[j], Sha256Hex(batch[j]), vectors[j]));
        }

        // 5) One atomic, idempotent write.
        var outcome = await store.ReplaceDocumentAsync(new DocumentToStore(
            req.DocumentKey, req.BlobName, eventId, embedder.ModelName, embedder.Dimensions,
            req.SourceVersionUtc, chunks), ct);

        logger.LogInformation("Ingested {Chunks} chunks, store outcome {StoreOutcome}", chunks.Count, outcome);
        return new(outcome == ReplaceOutcome.Stored ? IngestionOutcome.Stored : IngestionOutcome.Superseded, chunks.Count);
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}