namespace AzureLab.Messaging.Messages;

public record RedeemRequestMessage(
    Guid RedeemTransactionId,
    Guid OfferId,
    string UserId,
    DateTimeOffset RequestedAtUtc);
