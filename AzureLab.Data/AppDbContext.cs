using Microsoft.EntityFrameworkCore;

namespace AzureLab.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<RedeemTransaction> RedeemTransactions => Set<RedeemTransaction>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<OfferNotificationLog> OfferNotificationLogs => Set<OfferNotificationLog>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<EventDocument> EventDocuments => Set<EventDocument>();
    public DbSet<RedeemProposal> RedeemProposals => Set<RedeemProposal>();
    public DbSet<OfferInventory> OfferInventories => Set<OfferInventory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Destination).HasMaxLength(200).IsRequired();
            b.Property(x => x.Payload).IsRequired();
            b.HasIndex(x => x.ProcessedUtc);          // for the dispatcher query
            b.HasIndex(x => new { x.ProcessedUtc, x.LockedUntilUtc, x.CreatedUtc });
        });

        modelBuilder.Entity<RedeemTransaction>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasMaxLength(50);
            b.HasIndex(x => x.OfferId);
        });

        modelBuilder.Entity<ProcessedMessage>(b =>
        {
            b.HasKey(x => new { x.Consumer, x.MessageId });
            b.Property(x => x.Consumer).HasMaxLength(200).IsRequired();
            b.Property(x => x.MessageId).HasMaxLength(200).IsRequired();
            b.Property(x => x.ProcessedUtc).IsRequired(false);
            b.HasIndex(x => x.ProcessedUtc);          // for the dispatcher query
        });

        modelBuilder.Entity<OfferNotificationLog>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.NotificationId).IsRequired();
            b.Property(x => x.OfferId).IsRequired();
            b.Property(x => x.EventId).IsRequired();
            b.Property(x => x.NotificationType).HasMaxLength(100).IsRequired();
            b.Property(x => x.CreatedUtc).IsRequired();
        });

        modelBuilder.Entity<EventDocument>(b =>
        {
            b.ToTable("EventDocuments");
            b.Property(x => x.BlobName).HasMaxLength(500).IsRequired();
            b.Property(x => x.ETag).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.BlobName, x.ETag }).IsUnique();
        });

        modelBuilder.Entity<RedeemProposal>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.UserId).HasMaxLength(200).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            b.Property(x => x.CorrelationId).HasMaxLength(64);
            b.HasIndex(x => new { x.UserId, x.OfferId })
                .IsUnique()
                .HasFilter("[Status] = 'Pending'");        // at most ONE open proposal per user+offer
            b.HasIndex(x => x.ExpiresUtc);
        });

        modelBuilder.Entity<OfferInventory>(b =>
        {
            b.HasKey(x => x.OfferId);
            b.Property(x => x.RowVersion)
                .IsRowVersion();
        });
    }
}
