namespace AzureLab.Messaging
{
    public sealed class ServiceBusOptions
    {
        public const string SectionName = "ServiceBus";

        //Local only (emulator)
        public string? ConnectionString { get; set; }

        //Azure only (managed identity)
        public string? FullyQualifiedNamespace { get; set; }

        public string RedeemQueue { get; set; } = "redeem-requests";
        public string OfferTopic { get; set; } = "offer-events";
    }
}
