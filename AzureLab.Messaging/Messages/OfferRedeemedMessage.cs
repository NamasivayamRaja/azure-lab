namespace AzureLab.Messaging.Messages;
public record OfferRedeemed(
    Guid RedeemId,
    Guid OfferId,
    Guid EventId,
    string UserId,
    DateTimeOffset RedeemedAtUtc);