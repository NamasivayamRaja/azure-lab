using AzureLab.Assistant;
using AzureLab.Data;
using Microsoft.EntityFrameworkCore;

namespace AzureLabs.Api.Assistant;

/// <summary>DEV ONLY. Proves single-use confirmation, replay, expiry, ownership and the tool guards, without any model.</summary>
public static class ProposalSelfTest
{
    public static IEndpointRouteBuilder MapProposalSelfTest(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/proposals/selftest", async (IServiceScopeFactory scopes, ToolLimits limits, CancellationToken ct) =>
        {
            var checks = new List<object>();
            var failed = 0;
            void Check(string name, bool pass, string detail = "")
            {
                if (!pass) failed++;
                checks.Add(new { name, pass, detail });
            }

            const string user = "12345";
            var ev = Guid.NewGuid();
            var offer = Guid.NewGuid();
            var allowed = new HashSet<Guid> { ev };
            CallerContext Caller(string u) => new(u, ev, allowed, "proposal-selftest");

            async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
            {
                using var scope = scopes.CreateScope();               // one DbContext per concurrent operation
                return await work(scope.ServiceProvider);
            }
            Task<RedeemProposalView> Propose(string u, Guid o) =>
                InScope(sp => sp.GetRequiredService<IRedeemProposals>().CreateOrGetOpenAsync(Caller(u), o, ct));
            Task<ConfirmResult> Confirm(Guid id, string u) =>
                InScope(sp => sp.GetRequiredService<ProposalConfirmationService>().ConfirmAsync(id, u, ct));
            Task<int> CountByProposal(Guid id) =>
                InScope(async sp =>
                {
                    var key = id.ToString();
                    return await sp.GetRequiredService<AppDbContext>().RedeemTransactions.CountAsync(t => t.CorrelationId == key, ct);
                });
            Task<int> CountByOffer(Guid o) =>
                InScope(async sp => await sp.GetRequiredService<AppDbContext>().RedeemTransactions.CountAsync(t => t.OfferId == o, ct));

            // 1. Idempotent propose under concurrency
            var proposed = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Propose(user, offer)));
            var pid = proposed[0].ProposalId;
            Check("10 parallel proposes for the same user+offer yield ONE open proposal",
                proposed.Select(x => x.ProposalId).Distinct().Count() == 1);

            // 2. Single-use confirmation under concurrency
            var par = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Confirm(pid, user)));
            var summary = string.Join(", ", par.GroupBy(r => r.Status).Select(g => $"{g.Key}={g.Count()}"));
            Check("10 parallel confirms: exactly ONE wins the claim", par.Count(r => r.Status == ConfirmStatus.Confirmed) == 1, summary);
            Check("no parallel confirm failed outright",
                par.All(r => r.Status is ConfirmStatus.Confirmed or ConfirmStatus.Replayed or ConfirmStatus.InProgress), summary);

            // 3. Replay returns the stored result
            var settled = new ConfirmResult(ConfirmStatus.InProgress);
            for (var i = 0; i < 20 && settled.Status != ConfirmStatus.Replayed; i++)
            {
                settled = await Confirm(pid, user);
                if (settled.Status != ConfirmStatus.Replayed) await Task.Delay(500, ct);
            }
            Check("confirming again returns the stored result (replay, same transaction)",
                settled.Status == ConfirmStatus.Replayed && settled.RedeemTransactionId is not null);
            var byProposal = await CountByProposal(pid);
            Check("exactly ONE RedeemTransaction carries the proposal id", byProposal == 1, $"count={byProposal}");

            // 4. Ownership: a foreign user cannot confirm (and cannot tell it exists)
            Check("foreign user gets NotFound", (await Confirm(pid, "99999")).Status == ConfirmStatus.NotFound);
            Check("unknown proposal id gets NotFound", (await Confirm(Guid.NewGuid(), user)).Status == ConfirmStatus.NotFound);

            // 5. Domain rule: the same offer may be redeemed several times, each time with a NEW proposal
            var second = await Propose(user, offer);
            Check("same offer can be proposed again after a confirmed redeem", second.ProposalId != pid);
            var c2 = await Confirm(second.ProposalId, user);
            Check("second proposal confirms with a DIFFERENT transaction",
                c2.Status == ConfirmStatus.Confirmed && c2.RedeemTransactionId != settled.RedeemTransactionId);
            var byOffer = await CountByOffer(offer);
            Check("the offer now has exactly two redeem transactions (one per proposal)", byOffer == 2, $"count={byOffer}");

            // 6. Expiry and slot release
            var offer2 = Guid.NewGuid();
            var p3 = await Propose(user, offer2);
            await InScope(sp => sp.GetRequiredService<AppDbContext>().RedeemProposals
                .Where(x => x.Id == p3.ProposalId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresUtc, DateTimeOffset.UtcNow.AddMinutes(-1)), ct));
            Check("expired proposal cannot be confirmed", (await Confirm(p3.ProposalId, user)).Status == ConfirmStatus.Expired);
            Check("expired proposal created NO redeem", await CountByProposal(p3.ProposalId) == 0);
            var p4 = await Propose(user, offer2);
            Check("a new proposal can be created after expiry (slot released)", p4.ProposalId != p3.ProposalId);

            // 7. Tool guards
            using var scope = scopes.CreateScope();
            var proposals = scope.ServiceProvider.GetRequiredService<IRedeemProposals>();
            var offer3 = Guid.NewGuid();

            var s1 = new AssistantRunState();
            var noMention = new AssistantTools(Caller(user), null!, null!, null!, limits, s1, proposals, "please help me");
            var r1 = await noMention.ProposeRedeemAsync(offer3.ToString(), ct);
            Check("ProposeRedeem rejects an offer id the user did not write (indirect-injection control)",
                !r1.Ok && r1.Error!.Code == ToolErrorCode.InvalidArgument && s1.Proposals.Count == 0);

            var s2 = new AssistantRunState();
            var mention = new AssistantTools(Caller(user), null!, null!, null!, limits, s2, proposals, $"redeem offer {offer3}");
            var r2 = await mention.ProposeRedeemAsync(offer3.ToString(), ct);
            var r3 = await mention.ProposeRedeemAsync(offer3.ToString(), ct);
            Check("ProposeRedeem returns a proposal for the model and creates NO transaction",
                r2.Ok && s2.Proposals.Count == 1 && await CountByOffer(offer3) == 0);
            Check("the model-visible result contains no proposal id",
                r2.Data is not null && !System.Text.Json.JsonSerializer.Serialize(r2.Data).Contains(s2.Proposals[0].ProposalId.ToString()));
            Check("only one proposal per request", !r3.Ok && r3.Error!.Code == ToolErrorCode.LimitReached);

            return Results.Ok(new { allPassed = failed == 0, failed, checks });
        });

        return app;
    }
}

