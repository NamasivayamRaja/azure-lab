public static class MessageTypes
{
    public const string RedeemRequested = "RedeemRequested";
    public const string OfferRedeemed = "OfferRedeemed";
    public const string OfferRedeemRejected = "OfferRedeemRejected";
}

public sealed record RedeemRequested(Guid ReedemId, Guid OfferId, Guid EventId, string UserId, DateTimeOffset RequestedUtc);
public sealed record OfferRedeemed(Guid ReedemId, Guid OfferId, Guid EventId, string UserId, DateTimeOffset RedeemedAtUtc);
public sealed record OfferRedeemRejected(Guid ReedemId, Guid OfferId, Guid EventId, string UserId, DateTimeOffset RejectedAtUtc);
