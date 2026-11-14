using System.ComponentModel;
using System.Text.Json.Serialization;

namespace AzureLab.Assistant;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntentKind { CheckRedeemStatus, AskEventQuestion, ListDocuments, RequestRedeem, Unknown }

/// <summary>UNTRUSTED: exactly what the model produced. Never used until IntentValidator accepts it.</summary>
public sealed record ExtractedIntent(
    [property: Description("The kind of request.")] IntentKind Intent,
    [property: Description("GUID of the redeem request if the user wrote one, otherwise an empty string.")] string RedeemId,
    [property: Description("GUID of the offer to redeem if the user wrote one, otherwise an empty string.")] string OfferId,
    [property: Description("For AskEventQuestion: the user's question about the event, otherwise an empty string.")] string Question);

/// <summary>TRUSTED: typed, validated, carries only the fields that belong to the intent.</summary>
public abstract record ValidatedIntent(IntentKind Kind);
public sealed record CheckRedeemStatusIntent(Guid RedeemId) : ValidatedIntent(IntentKind.CheckRedeemStatus);
public sealed record AskEventQuestionIntent(string Question) : ValidatedIntent(IntentKind.AskEventQuestion);
public sealed record ListDocumentsIntent() : ValidatedIntent(IntentKind.ListDocuments);
public sealed record RequestRedeemIntent(Guid OfferId) : ValidatedIntent(IntentKind.RequestRedeem);
public sealed record UnknownIntent(string? MissingField = null) : ValidatedIntent(IntentKind.Unknown);