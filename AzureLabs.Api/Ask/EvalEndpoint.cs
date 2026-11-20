using System.Text.Json;
using AzureLab.Rag.Query;

namespace AzureLabs.Api.Ask;

public sealed record EvalCase(string Question, bool ExpectAnswer, string[]? AnyOf, string? Note);
public sealed record EvalFile(Guid EventId, string ExpectedSource, EvalCase[] Cases);

public static class EvalEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Dev only. Call from the IsDevelopment() block.</summary>
    public static IEndpointRouteBuilder MapEvalEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dev/rag/eval", async (string? label, string? name,
            EventQuestionService service, IHostEnvironment env, CancellationToken ct) =>
        {
            var file = Path.GetFileName(name ?? "handbook-eval.json");
            var path = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "infra", "local", "eval", file));
            if (!File.Exists(path)) return Results.NotFound($"Eval file not found: {path}");

            var set = JsonSerializer.Deserialize<EvalFile>(await File.ReadAllTextAsync(path, ct), JsonOpts)!;
            var rows = new List<object>();
            var passed = 0;
            var expectedAnswerable = 0;
            var sourceHits = 0;
            long sumEmbedMs = 0, sumSearchMs = 0, sumGenerateMs = 0, sumInTokens = 0, sumOutTokens = 0;

            foreach (var c in set.Cases)
            {
                var r = await service.AskAsync(set.EventId, c.Question, ct);
                bool ok; string why;
                var hasSource = r.Citations.Any(x => x.BlobName.EndsWith(set.ExpectedSource, StringComparison.OrdinalIgnoreCase));

                if (c.ExpectAnswer)
                {
                    expectedAnswerable++;
                    if (hasSource) sourceHits++;
                }

                if (!c.ExpectAnswer)
                {
                    ok = !r.Answered;
                    why = ok ? "refused as expected" : "ANSWERED but should refuse (possible hallucination)";
                }
                else if (!r.Answered)
                {
                    ok = false;
                    why = $"refused ({r.Outcome}) but the answer exists in the document";
                }
                else
                {
                    var hasFact = c.AnyOf is null || c.AnyOf.Any(k => r.Answer.Contains(k, StringComparison.OrdinalIgnoreCase));
                    ok = hasFact && hasSource;
                    why = ok ? "fact and source found" : !hasFact ? "answer lacks the expected fact" : "expected source not cited";
                }

                if (r.Diagnostics is { } d)
                {
                    sumEmbedMs += d.EmbedMs;
                    sumSearchMs += d.SearchMs;
                    sumGenerateMs += d.GenerateMs;
                    sumInTokens += d.InputTokens ?? 0;
                    sumOutTokens += d.OutputTokens ?? 0;
                }

                if (ok) passed++;
                rows.Add(new
                {
                    c.Question,
                    c.ExpectAnswer,
                    Pass = ok,
                    SourceHit = hasSource,
                    Why = why,
                    r.Outcome,
                    Top = r.Diagnostics?.TopSimilarity is { } s ? Math.Round(s, 3) : (double?)null,
                    EmbedMs = r.Diagnostics?.EmbedMs,
                    SearchMs = r.Diagnostics?.SearchMs,
                    GenerateMs = r.Diagnostics?.GenerateMs,
                    InputTokens = r.Diagnostics?.InputTokens,
                    OutputTokens = r.Diagnostics?.OutputTokens,
                    Answer = r.Answer
                });
            }

            var count = set.Cases.Length;
            return Results.Ok(new
            {
                label,
                ranAtUtc = DateTime.UtcNow,
                passed,
                total = count,
                sourceHits,
                expectedAnswerable,
                sourceHitRate = expectedAnswerable > 0 ? Math.Round((double)sourceHits / expectedAnswerable, 3) : 1.0,
                avgEmbedMs = count > 0 ? sumEmbedMs / count : 0,
                avgSearchMs = count > 0 ? sumSearchMs / count : 0,
                avgGenerateMs = count > 0 ? sumGenerateMs / count : 0,
                totalInputTokens = sumInTokens,
                totalOutputTokens = sumOutTokens,
                rows
            });
        });
        return app;
    }
}