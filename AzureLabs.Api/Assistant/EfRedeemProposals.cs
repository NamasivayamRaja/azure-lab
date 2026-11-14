using AzureLab.Assistant;
using AzureLab.Data;
using Microsoft.EntityFrameworkCore;

namespace AzureLabs.Api.Assistant;

public sealed class EfRedeemProposals(AppDbContext db) : IRedeemProposals
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public async Task<RedeemProposalView> CreateOrGetOpenAsync(ICallerContext caller, Guid offerId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var open = await FindOpenAsync(caller.UserId, offerId, ct);
        if (open is not null && open.ExpiresUtc > now) return ToView(open);

        if (open is not null)   // expired but still Pending: free the unique slot
        {
            await db.RedeemProposals
                .Where(x => x.Id == open.Id && x.Status == RedeemProposalStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RedeemProposalStatus.Expired), ct);
        }

        var p = new RedeemProposal
        {
            Id = Guid.NewGuid(),
            UserId = caller.UserId,
            OfferId = offerId,
            EventId = caller.EventId,
            Status = RedeemProposalStatus.Pending,
            CreatedUtc = now,
            ExpiresUtc = now + Ttl,
            CorrelationId = caller.CorrelationId
        };
        db.RedeemProposals.Add(p);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DBErrors.IsUniqueViolation(ex))
        {
            // A concurrent request created the open proposal first: return that one (idempotent).
            db.Entry(p).State = EntityState.Detached;
            var winner = await FindOpenAsync(caller.UserId, offerId, ct);
            if (winner is null) throw;
            return ToView(winner);
        }
        return ToView(p);
    }

    private Task<RedeemProposal?> FindOpenAsync(string userId, Guid offerId, CancellationToken ct) =>
        db.RedeemProposals.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.OfferId == offerId
                                      && x.Status == RedeemProposalStatus.Pending, ct);

    // The summary is built by the SERVER from validated data. The model's prose is never what the human confirms.
    private static RedeemProposalView ToView(RedeemProposal p) =>
        new(p.Id, p.OfferId, $"Redeem offer {p.OfferId} for user {p.UserId}", p.ExpiresUtc, "awaiting_user_confirmation");
}

