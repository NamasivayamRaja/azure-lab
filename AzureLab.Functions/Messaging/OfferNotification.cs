namespace AzureLab_Functions.Messaging
{
    public sealed record OfferNotification(Guid NotificationId, Guid OfferId, 
        Guid EventId, string NotificationType, DateTime OccuredUtc);

}
