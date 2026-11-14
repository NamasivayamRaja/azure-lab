namespace AzureLab.Data;

public sealed class OfferInventory
{
    public Guid OfferId { get; set; }
    public int Capacity { get; set; }
    public int Remaining { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = default!;
}
