using AzureLab.Rag.Abstractions;

namespace AzureLab.Rag.Providers.ContentSafety;

internal sealed class NoOpContentSafetyGate : IContentSafetyGate
{
    public Task<PromptShieldDecision> ShieldAsync(
        string? userPrompt,
        IReadOnlyList<string> documents,
        CancellationToken ct = default) =>
        Task.FromResult(PromptShieldDecision.Skipped());

    public Task<ModerationDecision> ModerateTextAsync(
        string text,
        CancellationToken ct = default) =>
        Task.FromResult(ModerationDecision.Skipped());
}
