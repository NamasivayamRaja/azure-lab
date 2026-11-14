namespace AzureLab.Data
{
    public sealed class ProcessedMessage
    {
        public string Consumer { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public DateTimeOffset? ProcessedUtc { get; set; }
    }
}
