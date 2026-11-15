namespace AzureLab.Data;

public sealed class EventEnrollment
{
    public Guid EventId { get; set; }
    public string UserId { get; set; } = default!;
    public DateTimeOffset EnrolledUtc { get; set; }
}
