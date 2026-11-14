using AzureLab.Assistant;
using Microsoft.Extensions.AI;

namespace AzureLabs.Api.Assistant;

/// <summary>DEV ONLY. Model-free proof of the authorization and validation rules: the tools are called directly.</summary>
public static class AssistantSelfTest
{
    public static IEndpointRouteBuilder MapAssistantSelfTest(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dev/assistant/selftest", async (
            Guid redeemId, string owner, string stranger,
            IRedeemStatusReader redeems, ToolLimits limits, CancellationToken ct) =>
        {
            var checks = new List<object>();
            var failed = 0;
            void Check(string name, bool pass, string detail = "")
            {
                if (!pass) failed++;
                checks.Add(new { name, pass, detail });
            }

            var docs = new RecordingCatalog();
            var search = new RecordingSearch();
            var eventA = Guid.NewGuid();
            var eventB = Guid.NewGuid();

            AssistantTools ToolsFor(string user, Guid scoped, params Guid[] allowed) =>
                new(new CallerContext(user, scoped, allowed.ToHashSet(), "selftest"),
                    redeems, docs, search, limits, new AssistantRunState());

            // 1. Ownership is enforced inside the SQL query (real database)
            var own = await ToolsFor(owner, eventA, eventA).GetRedeemStatusAsync(redeemId.ToString(), ct);
            Check("owner can read own redeem", own.Ok && own.Data!.RedeemId == redeemId,
                  own.Ok ? own.Data!.Status : own.Error?.Message ?? "");

            var foreign = await ToolsFor(stranger, eventA, eventA).GetRedeemStatusAsync(redeemId.ToString(), ct);
            var missing = await ToolsFor(stranger, eventA, eventA).GetRedeemStatusAsync(Guid.NewGuid().ToString(), ct);
            Check("stranger gets not_found for someone else's redeem",
                  !foreign.Ok && foreign.Error!.Code == ToolErrorCode.NotFound);
            Check("foreign and nonexistent ids are indistinguishable (no existence oracle)",
                  foreign.Error?.Code == missing.Error?.Code && foreign.Error?.Message == missing.Error?.Message);

            // 2. Argument fuzzing: every malformed redeemId is rejected before any data access
            var bad = new Dictionary<string, string?>
            {
                ["fabricated"] = "unknown",
                ["empty"] = "",
                ["null"] = null,
                ["braces"] = $"{{{redeemId}}}",
                ["nilGuid"] = Guid.Empty.ToString(),
                ["sqlInjection"] = $"{redeemId}' OR 1=1 --",
                ["huge"] = new string('A', 100_000),
                ["unicodeDashes"] = redeemId.ToString().Replace("-", "\u2010")
            };
            foreach (var (name, value) in bad)
            {
                var r = await ToolsFor(owner, eventA, eventA).GetRedeemStatusAsync(value!, ct);
                Check($"invalid redeemId '{name}' rejected", !r.Ok && r.Error!.Code == ToolErrorCode.InvalidArgument);
            }

            // 3. Event scope: a caller without access to the scoped event never reaches the ports
            var denied = ToolsFor(owner, eventA, eventB);       // scoped to A, allowed only B
            var d1 = await denied.ListEventDocumentsAsync(ct);
            var d2 = await denied.SearchEventDocumentsAsync("anything", ct);
            Check("no access to scoped event: List -> not_found, port not called",
                  !d1.Ok && d1.Error!.Code == ToolErrorCode.NotFound && docs.Calls == 0);
            Check("no access to scoped event: Search -> not_found, port not called",
                  !d2.Ok && d2.Error!.Code == ToolErrorCode.NotFound && search.Calls == 0);

            // 4. With access: only the server-scoped eventId reaches the ports; results are capped
            var allowed = ToolsFor(owner, eventA, eventA);
            var l = await allowed.ListEventDocumentsAsync(ct);
            var s = await allowed.SearchEventDocumentsAsync("registration time", ct);
            Check("ports receive only the server-scoped eventId",
                  docs.LastEventId == eventA && search.LastEventId == eventA);
            Check("document list is capped and flagged truncated",
                  l.Ok && l.Data!.Items.Count == limits.MaxListItems && l.Data.Truncated);
            Check("passages are capped in count and size",
                  s.Ok && s.Data!.Passages.Count <= limits.MaxPassages &&
                  s.Data.Passages.All(p => p.Text.Length <= limits.MaxPassageChars + 1));

            // 5. Search query validation happens before the port is called
            search.Calls = 0;
            var q1 = await allowed.SearchEventDocumentsAsync("", ct);
            var q2 = await allowed.SearchEventDocumentsAsync(new string('x', limits.MaxQueryChars + 1), ct);
            var q3 = await allowed.SearchEventDocumentsAsync("hello\u0000world", ct);
            Check("empty, oversized and control-character queries rejected, port not called",
                  new[] { q1, q2, q3 }.All(r => !r.Ok && r.Error!.Code == ToolErrorCode.InvalidArgument) && search.Calls == 0);

            // 6. No tool schema exposes identity parameters to the model
            var forbidden = new[] { "eventid", "userid", "tenantid", "user", "event" };
            var leaks = allowed.CreateTools().OfType<AIFunctionDeclaration>()
                .SelectMany(t => t.JsonSchema.TryGetProperty("properties", out var p)
                    ? p.EnumerateObject().Select(x => $"{t.Name}.{x.Name}")
                    : Enumerable.Empty<string>())
                .Where(n => forbidden.Contains(n.Split('.')[1].ToLowerInvariant()))
                .ToList();
            Check("no tool schema exposes identity parameters", leaks.Count == 0, string.Join(",", leaks));

            return Results.Ok(new { allPassed = failed == 0, failed, checks });
        });

        return app;
    }

    private sealed class RecordingCatalog : IEventDocumentCatalog
    {
        public int Calls;
        public Guid LastEventId;
        public Task<IReadOnlyList<EventDocumentView>> ListAsync(Guid eventId, int max, CancellationToken ct)
        {
            Calls++;
            LastEventId = eventId;
            IReadOnlyList<EventDocumentView> items = Enumerable.Range(1, max)
                .Select(i => new EventDocumentView($"doc{i}.pdf", "Processed", 1000, DateTimeOffset.UtcNow))
                .ToList();
            return Task.FromResult(items);
        }
    }

    private sealed class RecordingSearch : IEventPassageSearch
    {
        public int Calls;
        public Guid LastEventId;
        public Task<IReadOnlyList<DocumentPassage>> SearchAsync(Guid eventId, string query, int maxPassages, CancellationToken ct)
        {
            Calls++;
            LastEventId = eventId;
            IReadOnlyList<DocumentPassage> hits = Enumerable.Range(1, maxPassages + 2)
                .Select(i => new DocumentPassage("handbook.pdf", i, 0.8, new string('x', 5000)))
                .ToList();
            return Task.FromResult(hits);
        }
    }
}