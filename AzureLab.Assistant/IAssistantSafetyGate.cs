namespace AzureLab.Assistant;

public sealed record AssistantSafetyCheck(bool Blocked, string? UnavailableReason = null);

public interface IAssistantSafetyGate
{
    Task<AssistantSafetyCheck> CheckInputAsync(string userMessage, CancellationToken ct);
    Task<AssistantSafetyCheck> CheckOutputAsync(string answer, CancellationToken ct);
}
