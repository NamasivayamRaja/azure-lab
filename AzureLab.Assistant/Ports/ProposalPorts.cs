namespace AzureLab.Assistant;

public sealed record RedeemProposalView(Guid ProposalId, Guid OfferId, string Summary, DateTimeOffset ExpiresUtc, string Status);

public interface IRedeemProposals
{
    /// <summary>Idempotent: returns the caller's open proposal for this offer, or creates it. NEVER executes anything.</summary>
    Task<RedeemProposalView> CreateOrGetOpenAsync(ICallerContext caller, Guid offerId, CancellationToken ct);
}

