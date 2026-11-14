namespace AzureLab.Data;

public enum RedeemProposalStatus { Pending, Confirming, Confirmed, Expired }

public class RedeemProposal
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public Guid OfferId { get; set; }
    public Guid EventId { get; set; }                       // event the assistant was scoped to (audit)
    public RedeemProposalStatus Status { get; set; } = RedeemProposalStatus.Pending;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? ClaimedUtc { get; set; }         // start of the confirm lease
    public DateTimeOffset? ConfirmedUtc { get; set; }
    public Guid? RedeemTransactionId { get; set; }
    public string? CorrelationId { get; set; }              // correlation of the assistant request that created it
}

