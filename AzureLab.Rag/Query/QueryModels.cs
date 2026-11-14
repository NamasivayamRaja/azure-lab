namespace AzureLab.Rag.Query;

public sealed record Citation(
    int Number, string BlobName, int ChunkIndex, double Similarity, string Snippet, bool CitedInAnswer);

public sealed record AskDiagnostics(
    int Candidates, int AboveThreshold, double? TopSimilarity,
    long EmbedMs, long SearchMs, long GenerateMs);

public sealed record AskResult(
    bool Answered, string Answer, IReadOnlyList<Citation> Citations, AskDiagnostics? Diagnostics, string Outcome);