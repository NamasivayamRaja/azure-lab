using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace AzureLab.Assistant;

public sealed record DocumentList(IReadOnlyList<EventDocumentView> Items, bool Truncated);
public sealed record ProposalOffer(string Status, string Summary, DateTimeOffset ExpiresUtc);   // what the MODEL sees: no proposal id
public sealed record PassageList(IReadOnlyList<DocumentPassage> Passages, string? Note);

/// <summary>
/// Built PER REQUEST around the authenticated caller. The model-visible schema contains only business
/// parameters: eventId and userId are constructor-injected and cannot appear in any tool schema.
/// </summary>
public sealed class AssistantTools(
    ICallerContext caller,
    IRedeemStatusReader redeems,
    IEventDocumentCatalog documents,
    IEventPassageSearch search,
    ToolLimits limits,
    AssistantRunState state,
    IRedeemProposals? proposals = null,
    string userMessage = "")
{
    public IList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(GetRedeemStatusAsync, name: "GetRedeemStatus", description: ToolDescriptions.GetRedeemStatus),
        AIFunctionFactory.Create(ListEventDocumentsAsync, name: "ListEventDocuments", description: ToolDescriptions.ListEventDocuments),
        AIFunctionFactory.Create(SearchEventDocumentsAsync, name: "SearchEventDocuments", description: ToolDescriptions.SearchEventDocuments),
        AIFunctionFactory.Create(ProposeRedeemAsync, name: "ProposeRedeem", description: ToolDescriptions.ProposeRedeem),
    ];

    public async Task<ToolResult<ProposalOffer>> ProposeRedeemAsync(
        [Description(ToolDescriptions.OfferId)] string offerId, CancellationToken ct)
    {
        if (!ArgumentGuard.TryParseId(offerId, out var id))
        {
            state.NoteArgs($"offerIdLength={offerId?.Length ?? 0} (invalid)");
            return Bad<ProposalOffer>(ToolErrorCode.InvalidArgument, "offerId must be a GUID copied from the user's message.");
        }

        state.NoteArgs($"offerId={id}");

        // Indirect-injection control: the id must come from the USER's own words, never from a document or a tool result.
        if (!userMessage.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            return Bad<ProposalOffer>(ToolErrorCode.InvalidArgument, "offerId must be an id the user wrote in their message.");

        if (!caller.MayReadScopedEvent())
            return Bad<ProposalOffer>(ToolErrorCode.NotFound, "Not available.");

        if (proposals is null || state.Proposals.Count >= 1)          // at most one proposal per request
            return Bad<ProposalOffer>(ToolErrorCode.LimitReached, "Only one proposal can be prepared per request.");

        var view = await proposals.CreateOrGetOpenAsync(caller, id, ct);
        state.AddProposal(view);                                       // the client gets the proposal id from here, never from model text
        return Ok(new ProposalOffer("awaiting_user_confirmation", view.Summary, view.ExpiresUtc));
    }

    public async Task<ToolResult<RedeemStatusView>> GetRedeemStatusAsync(
        [Description(ToolDescriptions.RedeemId)] string redeemId, CancellationToken ct)
    {
        if (!ArgumentGuard.TryParseId(redeemId, out var id))
        {
            state.NoteArgs($"redeemIdLength={redeemId?.Length ?? 0} (invalid)");
            return Bad<RedeemStatusView>(ToolErrorCode.InvalidArgument,
                "redeemId must be a GUID like 3f2b8c1e-5d4a-4c1b-9e7a-2b6f1d0c9a11.");
        }

        state.NoteArgs($"redeemId={id}");
        // Ownership is inside the query: "not yours" and "does not exist" are indistinguishable.
        var view = await redeems.FindForUserAsync(id, caller.UserId, ct);
        return view is null
            ? Bad<RedeemStatusView>(ToolErrorCode.NotFound, "No redeem request with that id was found for the current user.")
            : Ok(view);
    }

    public async Task<ToolResult<DocumentList>> ListEventDocumentsAsync(CancellationToken ct)
    {
        state.NoteArgs("");
        if (!caller.MayReadScopedEvent())
            return Bad<DocumentList>(ToolErrorCode.NotFound, "No documents found.");

        var items = await documents.ListAsync(caller.EventId, limits.MaxListItems + 1, ct);
        var truncated = items.Count > limits.MaxListItems;
        return Ok(new DocumentList(items.Take(limits.MaxListItems).ToList(), truncated));
    }

    public async Task<ToolResult<PassageList>> SearchEventDocumentsAsync(
        [Description(ToolDescriptions.Query)] string query, CancellationToken ct)
    {
        if (!ArgumentGuard.TryNormalizeQuery(query, limits.MaxQueryChars, out var q, out var problem))
        {
            state.NoteArgs($"queryLength={query?.Length ?? 0} (invalid)");   // length only, never the text
            return Bad<PassageList>(ToolErrorCode.InvalidArgument, problem);
        }

        state.NoteArgs($"queryLength={q.Length}");
        if (!caller.MayReadScopedEvent())
            return Bad<PassageList>(ToolErrorCode.NotFound, "No documents found.");

        var hits = await search.SearchAsync(caller.EventId, q, limits.MaxPassages, ct);
        var passages = hits.Take(limits.MaxPassages)
            .Select(h => h with { Text = Truncate(h.Text, limits.MaxPassageChars) })
            .ToList();

        return Ok(new PassageList(passages, passages.Count == 0 ? "No relevant passages were found." : null));
    }

    private ToolResult<T> Ok<T>(T data)
    {
        state.NoteOutcome("ok");
        return ToolResult<T>.Success(data);
    }

    private ToolResult<T> Bad<T>(ToolErrorCode code, string message)
    {
        state.NoteOutcome(code switch
        {
            ToolErrorCode.InvalidArgument => "invalid_argument",
            ToolErrorCode.NotFound => "not_found",
            _ => "limit_reached"
        });
        return ToolResult<T>.Fail(code, message);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}