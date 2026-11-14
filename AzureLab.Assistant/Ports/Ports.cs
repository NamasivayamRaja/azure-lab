namespace AzureLab.Assistant;

public sealed record RedeemStatusView(Guid RedeemId, string Status, DateTimeOffset RequestedAtUtc);
public sealed record EventDocumentView(string FileName, string Status, long SizeBytes, DateTimeOffset ProcessedUtc);
public sealed record DocumentPassage(string FileName, int ChunkIndex, double Similarity, string Text);

public interface IRedeemStatusReader
{
    /// <summary>Ownership is part of the query: returns null for "not yours" and "does not exist" alike.</summary>
    Task<RedeemStatusView?> FindForUserAsync(Guid redeemId, string userId, CancellationToken ct);
}

public interface IEventDocumentCatalog
{
    Task<IReadOnlyList<EventDocumentView>> ListAsync(Guid eventId, int max, CancellationToken ct);
}

public interface IEventPassageSearch
{
    /// <summary>Retrieval only, no generation. Applies the existing similarity threshold.</summary>
    Task<IReadOnlyList<DocumentPassage>> SearchAsync(Guid eventId, string query, int maxPassages, CancellationToken ct);
}