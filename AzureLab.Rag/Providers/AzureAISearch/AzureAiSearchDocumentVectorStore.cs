using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using AzureLab.Rag.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureLab.Rag.Providers.AzureAISearch;

internal sealed class AzureAiSearchDocumentVectorStore(
    SearchClient searchClient,
    IOptions<AiOptions> options,
    ILogger<AzureAiSearchDocumentVectorStore> logger) : IDocumentVectorStore
{
    private const int BatchSize = 500;
    // Two-leg RRF (BM25 + vector) with Azure AI Search's default k=60: max score is 1/61 + 1/61.
    private const double MaxTwoLegRrfScore = 2.0 / 61.0;

    public async Task<ReplaceOutcome> ReplaceDocumentAsync(DocumentToStore doc, CancellationToken ct)
    {
        foreach (var c in doc.Chunks)
        {
            if (c.Embedding.Length != doc.EmbeddingDimensions)
                throw new InvalidOperationException(
                    $"Chunk {c.ChunkIndex} has {c.Embedding.Length} dimensions, expected {doc.EmbeddingDimensions}.");
        }

        // 1) Inspect existing chunks for this (eventId, blobName) to detect superseded versions and stale IDs.
        var existing = await LoadExistingMetadataAsync(doc.EventId, doc.BlobName, ct);

        var incomingUtc = doc.SourceVersionUtc.ToUniversalTime();
        var hasNewer = existing.Any(e =>
            !string.Equals(e.DocumentKey, doc.DocumentKey, StringComparison.Ordinal) &&
            e.SourceVersionUtc.ToUniversalTime() > incomingUtc);

        if (hasNewer)
        {
            logger.LogInformation("Superseded: newer version already indexed in AI Search, skipping {Key}", doc.DocumentKey);
            return ReplaceOutcome.Superseded;
        }

        // 2) Upsert all new chunks FIRST (zero read-gap; deterministic keys guarantee idempotency).
        var incomingDocs = new List<SearchChunkDocument>(doc.Chunks.Count);
        var keepIds = new HashSet<string>(StringComparer.Ordinal);
        var eventIdStr = doc.EventId.ToString("D");

        foreach (var c in doc.Chunks)
        {
            var id = SearchChunkDocument.ComputeKey(doc.DocumentKey, c.ChunkIndex);
            keepIds.Add(id);
            incomingDocs.Add(new SearchChunkDocument
            {
                Id = id,
                EventId = eventIdStr,
                DocumentKey = doc.DocumentKey,
                BlobName = doc.BlobName,
                ChunkIndex = c.ChunkIndex,
                Content = c.Content,
                ContentHash = c.ContentHash,
                Embedding = c.Embedding.ToArray(),
                EmbeddingModel = doc.EmbeddingModel,
                SourceVersionUtc = incomingUtc
            });
        }

        foreach (var batch in incomingDocs.Chunk(BatchSize))
        {
            var response = await searchClient.MergeOrUploadDocumentsAsync(batch, cancellationToken: ct);
            EnsureBatchSucceeded(response.Value, "MergeOrUpload");
        }

        // 3) Delete older ETag chunks AND leftover chunk indexes of the same DocumentKey.
        var staleIds = existing
            .Select(e => e.Id)
            .Where(id => !keepIds.Contains(id))
            .ToList();

        if (staleIds.Count > 0)
        {
            foreach (var batch in staleIds.Chunk(BatchSize))
            {
                var response = await searchClient.DeleteDocumentsAsync("id", batch, cancellationToken: ct);
                EnsureBatchSucceeded(response.Value, "Delete");
            }
        }

        logger.LogInformation(
            "Stored {Count} chunks for {Key} in AI Search (removed {Stale} stale chunks)",
            incomingDocs.Count, doc.DocumentKey, staleIds.Count);
        return ReplaceOutcome.Stored;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        Guid eventId, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken ct, string? queryText = null)
    {
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));

        var cfg = options.Value.VectorStore.AzureAISearch;
        var hasText = !string.IsNullOrWhiteSpace(queryText);
        var effectiveMode = hasText ? cfg.SearchMode : AzureAiSearchMode.VectorOnly;

        // Mandatory server-side security-trimming filter using typed Guid + SDK OData parameterization.
        var filter = SearchFilter.Create($"eventId eq {eventId.ToString("D")}");

        var searchOptions = new SearchOptions
        {
            Filter = filter,
            Size = topK,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryEmbedding)
                    {
                        KNearestNeighborsCount = topK,
                        Fields = { "embedding" }
                    }
                }
            }
        };

        searchOptions.Select.Add("id");
        searchOptions.Select.Add("documentKey");
        searchOptions.Select.Add("blobName");
        searchOptions.Select.Add("chunkIndex");
        searchOptions.Select.Add("content");

        string? searchText = null;
        if (effectiveMode is AzureAiSearchMode.Hybrid or AzureAiSearchMode.HybridSemantic)
        {
            searchText = queryText;
        }

        if (effectiveMode == AzureAiSearchMode.HybridSemantic)
        {
            searchOptions.QueryType = SearchQueryType.Semantic;
            searchOptions.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = cfg.SemanticConfigurationName
            };
        }

        var response = await searchClient.SearchAsync<SearchChunkDocument>(searchText, searchOptions, ct);
        var hits = new List<RetrievedChunk>();

        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(ct))
        {
            var doc = result.Document;
            var similarity = NormalizeScore(effectiveMode, result.Score, result.SemanticSearch?.RerankerScore);
            hits.Add(new RetrievedChunk(doc.DocumentKey, doc.BlobName, doc.ChunkIndex, doc.Content, similarity));
        }

        return hits;
    }

    private async Task<List<SearchChunkDocument>> LoadExistingMetadataAsync(
        Guid eventId, string blobName, CancellationToken ct)
    {
        var filter = SearchFilter.Create($"eventId eq {eventId.ToString("D")} and blobName eq {blobName}");
        var searchOptions = new SearchOptions
        {
            Filter = filter,
            Size = 1000
        };
        searchOptions.Select.Add("id");
        searchOptions.Select.Add("documentKey");
        searchOptions.Select.Add("chunkIndex");
        searchOptions.Select.Add("sourceVersionUtc");

        var response = await searchClient.SearchAsync<SearchChunkDocument>("*", searchOptions, ct);
        var results = new List<SearchChunkDocument>();
        await foreach (var item in response.Value.GetResultsAsync().WithCancellation(ct))
        {
            results.Add(item.Document);
        }
        return results;
    }

    private static double NormalizeScore(AzureAiSearchMode mode, double? rawScore, double? rerankerScore)
    {
        return mode switch
        {
            // Semantic reranker returns [0, 4]; normalize to [0, 1].
            AzureAiSearchMode.HybridSemantic when rerankerScore is { } rs => Math.Clamp(rs / 4.0, 0.0, 1.0),
            // Two-leg RRF returns ~(0, 2/61]; normalize to [0, 1].
            AzureAiSearchMode.Hybrid or AzureAiSearchMode.HybridSemantic =>
                Math.Clamp((rawScore ?? 0.0) / MaxTwoLegRrfScore, 0.0, 1.0),
            // Vector-only cosine score is already in [0, 1].
            _ => Math.Clamp(rawScore ?? 0.0, 0.0, 1.0)
        };
    }

    private static void EnsureBatchSucceeded(IndexDocumentsResult result, string operation)
    {
        var failed = result.Results.Where(r => !r.Succeeded).ToList();
        if (failed.Count == 0) return;

        var first = failed[0];
        throw new InvalidOperationException(
            $"Azure AI Search {operation} failed for {failed.Count} chunk(s). First error [{first.Status}] on key '{first.Key}': {first.ErrorMessage}");
    }
}
