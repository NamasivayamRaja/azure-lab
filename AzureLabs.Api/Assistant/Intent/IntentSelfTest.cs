using AzureLab.Assistant;
using Microsoft.Extensions.AI;

namespace AzureLabs.Api.Assistant;

/// <summary>DEV ONLY. Proves the validator and schema without any model.</summary>
public static class IntentSelfTest
{
    public static IEndpointRouteBuilder MapIntentSelfTest(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dev/assistant/intent-selftest", () =>
        {
            var checks = new List<object>();
            var failed = 0;
            void Check(string name, bool pass, string detail = "")
            {
                if (!pass) failed++;
                checks.Add(new { name, pass, detail });
            }

            const int max = 500;
            var rid = Guid.NewGuid();
            var oid = Guid.NewGuid();
            IntentValidation V(ExtractedIntent? x, string msg) => IntentValidator.Validate(x, msg, max);
            ExtractedIntent E(IntentKind k, string r = "", string o = "", string q = "") => new(k, r, o, q);

            Check("valid CheckRedeemStatus",
                V(E(IntentKind.CheckRedeemStatus, r: rid.ToString()), $"status of redeem {rid} please").Intent
                    is CheckRedeemStatusIntent c && c.RedeemId == rid);
            Check("id comparison ignores case",
                V(E(IntentKind.CheckRedeemStatus, r: rid.ToString()), $"redeem {rid.ToString().ToUpperInvariant()}").IsValid);
            Check("fabricated id (not in the message) rejected",
                !V(E(IntentKind.CheckRedeemStatus, r: Guid.NewGuid().ToString()), $"status of redeem {rid}").IsValid);
            Check("'unknown' as id rejected when the message HAS a GUID",
                !V(E(IntentKind.CheckRedeemStatus, r: "unknown"), $"status of redeem {rid}").IsValid);
            Check("empty id rejected when the message HAS a GUID",
                !V(E(IntentKind.CheckRedeemStatus), $"status of redeem {rid}").IsValid);
            Check("RequestRedeem with offer id in message accepted",
                V(E(IntentKind.RequestRedeem, o: oid.ToString()), $"please redeem offer {oid}").Intent is RequestRedeemIntent);
            Check("no GUID in message: CheckRedeemStatus downgraded to Unknown(redeemId)",
                V(E(IntentKind.CheckRedeemStatus, r: "unknown"), "what is the status of my redeem?").Intent
                    is UnknownIntent { MissingField: "redeemId" });
            Check("no GUID in message: RequestRedeem downgraded to Unknown(offerId)",
                V(E(IntentKind.RequestRedeem), "redeem it for me").Intent is UnknownIntent { MissingField: "offerId" });
            Check("AskEventQuestion valid",
                V(E(IntentKind.AskEventQuestion, q: "When does registration open?"), "When does registration open?").Intent
                    is AskEventQuestionIntent);
            Check("AskEventQuestion empty rejected", !V(E(IntentKind.AskEventQuestion), "hello").IsValid);
            Check("AskEventQuestion oversized rejected",
                !V(E(IntentKind.AskEventQuestion, q: new string('x', max + 1)), "hello").IsValid);
            Check("AskEventQuestion with control character rejected",
                !V(E(IntentKind.AskEventQuestion, q: "a\u0000b"), "hello").IsValid);
            Check("unrelated fields are dropped (ListDocuments carries nothing)",
                V(E(IntentKind.ListDocuments, r: "junk", q: "junk"), "which documents?").Intent is ListDocumentsIntent);
            Check("Unknown accepted as a legitimate answer", V(E(IntentKind.Unknown), "write a poem").Intent is UnknownIntent);
            Check("out-of-range enum value rejected", !V(E((IntentKind)99), "x").IsValid);
            Check("null extraction rejected", !V(null, "x").IsValid);

            var schema = (IntentExtractor.Format as ChatResponseFormatJson)?.Schema?.ToString() ?? "";
            Check("schema lists every intent value", Enum.GetNames<IntentKind>().All(n => schema.Contains(n)));
            Check("schema contains all four business fields and a required list",
                new[] { "intent", "redeemId", "offerId", "question" }.All(f => schema.Contains($"\"{f}\"")) && schema.Contains("\"required\""));
            Check("schema exposes no identity fields",
                !schema.Contains("userId", StringComparison.OrdinalIgnoreCase) &&
                !schema.Contains("eventId", StringComparison.OrdinalIgnoreCase));

            return Results.Ok(new { allPassed = failed == 0, failed, checks, schema });
        });

        return app;
    }
}