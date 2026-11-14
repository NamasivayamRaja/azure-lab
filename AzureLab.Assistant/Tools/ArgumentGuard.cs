namespace AzureLab.Assistant;

/// <summary>
/// Bounds the SHAPE of model-supplied arguments. It is not a SQL or prompt sanitizer:
/// SQL safety comes from parameterised queries in the adapters, and prompt injection
/// is handled by architecture (no write tools, server-side scope), not by string filters.
/// </summary>
public static class ArgumentGuard
{
    public static bool TryParseId(string? raw, out Guid id)
    {
        id = Guid.Empty;
        var s = raw?.Trim();
        return s is { Length: 36 } && Guid.TryParseExact(s, "D", out id) && id != Guid.Empty;
    }

    public static bool TryNormalizeQuery(string? raw, int maxChars, out string query, out string problem)
    {
        query = string.Empty;
        problem = string.Empty;
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) { problem = "query is required."; return false; }
        if (s.Length > maxChars) { problem = $"query must be at most {maxChars} characters."; return false; }
        if (s.Any(char.IsControl)) { problem = "query must not contain control characters."; return false; }
        query = s;
        return true;
    }
}