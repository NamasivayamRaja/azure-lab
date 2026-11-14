namespace AzureLab.Data
{
    public sealed class EventDocument
    {
        public long Id { get; set; }
        public string BlobName { get; set; } = default!;
        public string ETag { get; set; } = default!;
        public Guid? EventId { get; set; }
        public string? ContentType { get; set; }
        public long SizeBytes { get; set; }
        public string Status { get; set; } = default!;
        public string? RejectedReason { get; set; }
        public DateTimeOffset ProcessedUtc { get; set; }
    }
}
