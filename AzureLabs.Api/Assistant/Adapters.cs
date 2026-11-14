using AzureLab.Assistant;
using AzureLab.Data;
using AzureLab.Rag;
using AzureLab.Rag.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureLabs.Api.Assistant;

public sealed class EfRedeemStatusReader(AppDbContext db) : IRedeemStatusReader
{
    public async Task<RedeemStatusView?> FindForUserAsync(Guid redeemId, string userId, CancellationToken ct)
    {
        var row = await db.RedeemTransactions.AsNoTracking()
            .Where(x => x.Id == redeemId && x.UserId == userId)      // ownership inside the query
            .Select(x => new { x.Id, x.Status, x.RequestedAtUtc })    // CorrelationId/OfferId deliberately not exposed
            .FirstOrDefaultAsync(ct);

        return row is null ? null : new RedeemStatusView(row.Id, row.Status.ToString(), row.RequestedAtUtc);
    }
}

public sealed class EfEventDocumentCatalog(AppDbContext db) : IEventDocumentCatalog
{
    public async Task<IReadOnlyList<EventDocumentView>> ListAsync(Guid eventId, int max, CancellationToken ct)
    {
        var rows = await db.EventDocuments.AsNoTracking()
            .Where(d => d.EventId == eventId)
            .OrderByDescending(d => d.ProcessedUtc)
            .Take(max)
            .Select(d => new { d.BlobName, d.Status, d.SizeBytes, d.ProcessedUtc })   // RejectedReason/ETag not exposed
            .ToListAsync(ct);

        return rows.Select(r => new EventDocumentView(Path.GetFileName(r.BlobName), r.Status, r.SizeBytes, r.ProcessedUtc))
                   .ToList();
    }
}

/// <summary>Retrieval only (no generation). Reuses ITextEmbedder, IDocumentVectorStore and the existing Ai:Query settings.</summary>
public sealed class RagPassageSearch(
    ITextEmbedder embedder, IDocumentVectorStore store, IOptions<AiOptions> options) : IEventPassageSearch
{
    public async Task<IReadOnlyList<DocumentPassage>> SearchAsync(
        Guid eventId, string query, int maxPassages, CancellationToken ct)
    {
        var q = options.Value.Query;
        var vector = await embedder.EmbedQueryAsync(query, ct);
        var hits = await store.SearchAsync(eventId, vector, q.TopK, ct);   // EventId filter enforced in SQL

        return hits.Where(h => h.Similarity >= q.MinSimilarity)
                   .Take(maxPassages)
                   .Select(h => new DocumentPassage(Path.GetFileName(h.BlobName), h.ChunkIndex, Math.Round(h.Similarity, 3), h.Content))
                   .ToList();
    }
}