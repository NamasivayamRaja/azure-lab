using System.Text.RegularExpressions;

namespace AzureLab.Assistant;

public sealed record IntentValidation(ValidatedIntent? Intent, string? Error)
{
    public bool IsValid => Intent is not null;
}

public static partial class IntentValidator
{
    // Error texts are fixed strings with no user data: safe to feed back to the model in the repair turn.
    public static IntentValidation Validate(ExtractedIntent? x, string userMessage, int maxQuestionChars)
    {
        if (x is null) return Fail("The output was empty.");

        switch (x.Intent)
        {
            case IntentKind.Unknown:
                return Ok(new UnknownIntent());

            case IntentKind.ListDocuments:
                return Ok(new ListDocumentsIntent());

            case IntentKind.CheckRedeemStatus:
                // Deterministic: no GUID anywhere in the message means the model cannot have copied one. Ask the user.
                if (!ContainsGuid(userMessage)) return Ok(new UnknownIntent("redeemId"));
                if (!ArgumentGuard.TryParseId(x.RedeemId, out var rid) || !Appears(userMessage, rid))
                    return Fail("redeemId must be a GUID copied exactly from the user's message. If the message has no redeem id, use the intent Unknown with empty strings.");
                return Ok(new CheckRedeemStatusIntent(rid));

            case IntentKind.RequestRedeem:
                if (!ContainsGuid(userMessage)) return Ok(new UnknownIntent("offerId"));
                if (!ArgumentGuard.TryParseId(x.OfferId, out var oid) || !Appears(userMessage, oid))
                    return Fail("offerId must be a GUID copied exactly from the user's message. If the message has no offer id, use the intent Unknown with empty strings.");
                return Ok(new RequestRedeemIntent(oid));

            case IntentKind.AskEventQuestion:
                if (!ArgumentGuard.TryNormalizeQuery(x.Question, maxQuestionChars, out var q, out var problem))
                    return Fail("question is invalid: " + problem);
                return Ok(new AskEventQuestionIntent(q));

            default:
                return Fail("intent must be one of the listed values.");
        }
    }

    private static bool ContainsGuid(string message) => GuidPattern().IsMatch(message);

    private static bool Appears(string message, Guid id) =>
        message.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private static IntentValidation Ok(ValidatedIntent i) => new(i, null);
    private static IntentValidation Fail(string e) => new(null, e);

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidPattern();
}