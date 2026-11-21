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

/// <summary>Retrieval only (no generation). Reuses ITextEmbedder, IDocumentVectorStore, IContentSafetyGate and Ai:Query settings.</summary>
public sealed class RagPassageSearch(
    ITextEmbedder embedder,
    IDocumentVectorStore store,
    IContentSafetyGate safetyGate,
    IOptions<AiOptions> options,
    ILogger<RagPassageSearch> logger) : IEventPassageSearch
{
    public async Task<IReadOnlyList<DocumentPassage>> SearchAsync(
        Guid eventId, string query, int maxPassages, CancellationToken ct)
    {
        var q = options.Value.Query;
        var cs = options.Value.ContentSafety;
        var vector = await embedder.EmbedQueryAsync(query, ct);
        var hits = await store.SearchAsync(eventId, vector, q.TopK, ct, query);   // EventId filter enforced in store

        var selected = hits.Where(h => h.Similarity >= q.MinSimilarity)
                           .Take(maxPassages)
                           .ToList();

        if (cs.Enabled && selected.Count > 0)
        {
            var shield = await safetyGate.ShieldAsync(null, selected.Select(h => h.Content).ToList(), ct);
            if (!shield.WasEvaluated)
            {
                if (cs.FailClosedOnDocuments)
                    throw new HttpRequestException($"Content Safety document shield failed: {shield.ErrorReason}");
            }
            else if (shield.AnyDocumentAttackDetected)
            {
                var attacked = shield.AttackedDocumentIndexes.ToHashSet();
                logger.LogWarning("assistant_passage_attack dropped={Dropped} total={Total} latencyMs={Ms}",
                    attacked.Count, selected.Count, shield.LatencyMs);
                selected = selected.Where((_, idx) => !attacked.Contains(idx)).ToList();
            }
        }

        return selected
            .Select(h => new DocumentPassage(Path.GetFileName(h.BlobName), h.ChunkIndex, Math.Round(h.Similarity, 3), h.Content))
            .ToList();
    }
}

public sealed class AssistantSafetyGateAdapter(
    IContentSafetyGate safetyGate,
    IOptions<AiOptions> options) : IAssistantSafetyGate
{
    public async Task<AssistantSafetyCheck> CheckInputAsync(string userMessage, CancellationToken ct)
    {
        var cs = options.Value.ContentSafety;
        if (!cs.Enabled) return new AssistantSafetyCheck(false);

        var decision = await safetyGate.ShieldAsync(userMessage, Array.Empty<string>(), ct);
        if (!decision.WasEvaluated)
            return cs.FailClosedOnInput
                ? new AssistantSafetyCheck(false, decision.ErrorReason ?? "unavailable")
                : new AssistantSafetyCheck(false);

        return new AssistantSafetyCheck(decision.UserPromptAttackDetected);
    }

    public async Task<AssistantSafetyCheck> CheckOutputAsync(string answer, CancellationToken ct)
    {
        var cs = options.Value.ContentSafety;
        if (!cs.Enabled) return new AssistantSafetyCheck(false);

        var decision = await safetyGate.ModerateTextAsync(answer, ct);
        if (!decision.WasEvaluated)
            return cs.FailClosedOnOutput
                ? new AssistantSafetyCheck(false, decision.ErrorReason ?? "unavailable")
                : new AssistantSafetyCheck(false);

        return new AssistantSafetyCheck(decision.Blocked);
    }
}