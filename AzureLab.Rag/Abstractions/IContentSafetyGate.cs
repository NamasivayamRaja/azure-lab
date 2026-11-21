namespace AzureLab.Rag.Abstractions;

public sealed record PromptShieldDecision(
    bool WasEvaluated,
    bool UserPromptAttackDetected,
    IReadOnlyList<int> AttackedDocumentIndexes,
    long LatencyMs,
    string? ErrorReason = null)
{
    public bool AnyDocumentAttackDetected => AttackedDocumentIndexes.Count > 0;
    public bool AnyAttackDetected => UserPromptAttackDetected || AnyDocumentAttackDetected;

    public static PromptShieldDecision Skipped() =>
        new(false, false, Array.Empty<int>(), 0, null);
}

public sealed record ModerationDecision(
    bool WasEvaluated,
    bool Blocked,
    IReadOnlyDictionary<string, int> CategorySeverities,
    long LatencyMs,
    string? ErrorReason = null)
{
    public static ModerationDecision Skipped() =>
        new(false, false, new Dictionary<string, int>(), 0, null);
}

public sealed class ContentSafetyUnavailableException(string stage, string reason)
    : Exception($"Content safety gate unavailable at {stage}: {reason}")
{
    public string Stage { get; } = stage;
    public string Reason { get; } = reason;
}

public interface IContentSafetyGate
{
    Task<PromptShieldDecision> ShieldAsync(
        string? userPrompt,
        IReadOnlyList<string> documents,
        CancellationToken ct = default);

    Task<ModerationDecision> ModerateTextAsync(
        string text,
        CancellationToken ct = default);
}
