namespace AzureLab.Assistant;

public sealed record ToolCallRecord(string Tool, string Args, string Outcome, long DurationMs, int ResultChars);

public enum RunFailure { None, Infrastructure, ToolCallLimit, TooManyToolErrors, Internal }

/// <summary>One per request. Tools record what THEY validated; the invoker reads it for the audit line.</summary>
public sealed class AssistantRunState
{
    private readonly List<ToolCallRecord> _calls = [];

    public IReadOnlyList<ToolCallRecord> Calls => _calls;
    public RunFailure Failure { get; internal set; }
    public string? FailureDetail { get; internal set; }
    public int ToolErrors { get; internal set; }
    public string PendingArgs { get; private set; } = "";
    public string PendingOutcome { get; private set; } = "unknown";

    public void BeginCall() { PendingArgs = ""; PendingOutcome = "unknown"; }
    public void NoteArgs(string args) => PendingArgs = args;
    public void NoteOutcome(string outcome) => PendingOutcome = outcome;
    internal void Add(ToolCallRecord record) => _calls.Add(record);

    private readonly List<RedeemProposalView> _proposals = [];
    public IReadOnlyList<RedeemProposalView> Proposals => _proposals;
    public void AddProposal(RedeemProposalView view) => _proposals.Add(view);
}