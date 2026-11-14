namespace AzureLab.Data
{
    public class RedeemTransaction
    {
        public Guid Id { get; set; }
        public Guid OfferId { get; set; }
        public string UserId { get; set; } = default!;
        public RedeemTransactionStatus Status { get; set; } = RedeemTransactionStatus.Pending;   // Pending → Processing → Completed / Failed
        public DateTimeOffset RequestedAtUtc { get; set; }
        public string? CorrelationId { get; set; }
    }

    public enum RedeemTransactionStatus
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Rejected
    }
}
