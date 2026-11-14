using System.Net;
using AzureLab.Data;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;

namespace AzureLabs.Api.Assistant;

public enum ConfirmStatus { Confirmed, Replayed, InProgress, Expired, NotFound, Unavailable }
public sealed record ConfirmResult(ConfirmStatus Status, Guid? RedeemTransactionId = null);
internal sealed record RedeemAccepted(Guid RedeemTransactionId);

/// <summary>
/// Single-use confirmation: atomic claim -> reconcile-by-correlation -> call the EXISTING redeem endpoint -> record the result.
/// Lives in the API project on purpose: the model-facing assembly has no code path to this.
/// </summary>
public sealed class ProposalConfirmationService(
    AppDbContext db, IHttpClientFactory httpFactory, IServer server, ILogger<ProposalConfirmationService> log)
{
    public const string ClientName = "redeem-loopback";
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);   // must stay well above the 10 s HTTP timeout

    public async Task<ConfirmResult> ConfirmAsync(Guid proposalId, string userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var p = await LoadAsync(proposalId, userId, ct);                  // scoped to the caller: no existence oracle
        if (p is null) return new(ConfirmStatus.NotFound);
        if (p.Status == RedeemProposalStatus.Confirmed) return new(ConfirmStatus.Replayed, p.RedeemTransactionId);
        if (p.Status == RedeemProposalStatus.Expired) return new(ConfirmStatus.Expired);

        // ATOMIC CLAIM: exactly one caller flips Pending -> Confirming (or re-claims an expired lease), even across instances.
        var cutoff = now - Lease;
        var claimed = await db.RedeemProposals
            .Where(x => x.Id == proposalId && x.UserId == userId &&
                        ((x.Status == RedeemProposalStatus.Pending && x.ExpiresUtc > now) ||
                         (x.Status == RedeemProposalStatus.Confirming && x.ClaimedUtc < cutoff)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, RedeemProposalStatus.Confirming)
                .SetProperty(x => x.ClaimedUtc, (DateTimeOffset?)now), ct);

        if (claimed == 0)
        {
            var current = await LoadAsync(proposalId, userId, ct);
            if (current is null) return new(ConfirmStatus.NotFound);
            if (current.Status == RedeemProposalStatus.Confirmed) return new(ConfirmStatus.Replayed, current.RedeemTransactionId);
            if (current.Status == RedeemProposalStatus.Confirming) return new(ConfirmStatus.InProgress);

            await db.RedeemProposals
                .Where(x => x.Id == proposalId && x.Status == RedeemProposalStatus.Pending && x.ExpiresUtc <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RedeemProposalStatus.Expired), ct);
            return new(ConfirmStatus.Expired);
        }

        // RECONCILE BEFORE CALLING: the existing endpoint stores X-Correlation-ID verbatim, so a redeem tagged with this
        // proposal id proves an earlier (ambiguous) attempt already created it. This is our idempotency, with no endpoint change.
        var key = proposalId.ToString();
        var txId = await FindRedeemAsync(key, p.UserId, p.OfferId, ct);

        if (txId is null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(LoopbackBase(), $"/offers/{p.OfferId}/redeem"))
                {
                    Content = JsonContent.Create(new { userId = p.UserId })
                };
                request.Headers.Add("X-Correlation-ID", key);

                using var response = await httpFactory.CreateClient(ClientName).SendAsync(request, ct);
                if (response.StatusCode != HttpStatusCode.Accepted)
                {
                    log.LogWarning("proposal_confirm_call_failed proposal={Proposal} status={Status}", proposalId, (int)response.StatusCode);
                    return new(ConfirmStatus.Unavailable);                // stays Confirming: lease + lookup make a retry safe
                }
                txId = (await response.Content.ReadFromJsonAsync<RedeemAccepted>(ct))?.RedeemTransactionId;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or Polly.ExecutionRejectedException)
            {
                log.LogError(ex, "proposal_confirm_call_error proposal={Proposal} exceptionType={Type}", proposalId, ex.GetType().Name);
                return new(ConfirmStatus.Unavailable);
            }

            txId ??= await FindRedeemAsync(key, p.UserId, p.OfferId, ct);
            if (txId is null) return new(ConfirmStatus.Unavailable);
        }

        // Short and must not be cancelled half-way: if it is missed anyway, lease + lookup complete it on the next confirm.
        await db.RedeemProposals
            .Where(x => x.Id == proposalId && x.Status == RedeemProposalStatus.Confirming)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, RedeemProposalStatus.Confirmed)
                .SetProperty(x => x.ConfirmedUtc, (DateTimeOffset?)DateTimeOffset.UtcNow)
                .SetProperty(x => x.RedeemTransactionId, txId), CancellationToken.None);

        log.LogInformation("proposal_confirmed proposal={Proposal} user={UserId} offer={Offer} tx={Tx}", proposalId, userId, p.OfferId, txId);
        return new(ConfirmStatus.Confirmed, txId);
    }

    private Task<RedeemProposal?> LoadAsync(Guid id, string userId, CancellationToken ct) =>
        db.RedeemProposals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

    // Also matches user and offer, so a foreign row that merely reuses the correlation id is never adopted.
    private Task<Guid?> FindRedeemAsync(string key, string userId, Guid offerId, CancellationToken ct) =>
        db.RedeemTransactions.AsNoTracking()
            .Where(t => t.CorrelationId == key && t.UserId == userId && t.OfferId == offerId)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

    // The target is the server's own bound address, NEVER the request's Host header (that would be attacker-controlled).
    private Uri LoopbackBase()
    {
        var addrs = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var pick = addrs.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                   ?? addrs.FirstOrDefault() ?? "http://localhost:5000";
        return new Uri(pick.Replace("://*", "://localhost").Replace("://+", "://localhost")
                           .Replace("://0.0.0.0", "://localhost").Replace("://[::]", "://localhost"));
    }
}

