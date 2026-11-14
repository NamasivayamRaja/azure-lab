namespace AzureLab.Data;

public class OutboxMessage
{
    public Guid Id { get; set; }
    public string Destination { get; set; } = string.Empty; // e.g. queue / topic name
    public string CorrelationId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;      // message type, e.g. "OfferRedeemed"
    public string Payload { get; set; } = default!;        // JSON
    public string? Properties { get; set; } // JSON of application-specific properties (optional)
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? ProcessedUtc { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public string? Error { get; set; }
    public int RetryCount { get; set; }
}