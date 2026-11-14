namespace AzureLab.Assistant;

/// <summary>Plain constants for now (no new config keys). Sized for a CPU-only 3B model and a small context window.</summary>
public sealed record ToolLimits(
    int MaxQueryChars,
    int MaxListItems = 15,
    int MaxPassages = 3,
    int MaxPassageChars = 500,
    int MaxResultChars = 2500,
    int MaxIterations = 4,            // includes the first request
    int MaxToolCalls = 3,
    int MaxToolErrors = 2,            // invalid-argument / schema-mismatch calls before we stop
    int PerToolTimeoutSeconds = 15,
    int OverallTimeoutSeconds = 120,
    int MaxOutputTokens = 300);