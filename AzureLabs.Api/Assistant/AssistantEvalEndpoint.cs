using System.Diagnostics;
using System.Text.Json;
using AzureLab.Assistant;
using AzureLab.Rag;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AzureLabs.Api.Assistant;

public sealed record AssistantEvalCase(
    string Id, string Category, string Message, string[] Allowed, bool NoToolOk, string? ArgCheck);
public sealed record AssistantEvalFile(AssistantEvalCase[] Cases);

public static class AssistantEvalEndpoint
{
    private const string FixedRedeemId = "5B84B400-B435-481D-A0D1-8DF503EC0C43";
    private const string FixedOfferId = "4798310C-FD29-43CE-82BA-21047F5A6759";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, string> Prompts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["current"] = AssistantService.SystemPrompt,
        ["minimal"] = "You are an assistant for one event. Use a tool only when the request needs one. " +
                      "If a required id is missing, ask the user for it. Never invent ids.",
        ["ask"] = AssistantService.SystemPrompt
            .Replace("Never invent an id.",
                     "If the user did not write the id, ask them for it. Never use a placeholder such as <id> or your_id.")
            .Replace("with the offer id the user wrote.",
                     "with the offer id the user wrote. If they did not write one, ask for it.")
    };

    /// <summary>Dev only. First-turn tool decision per model x prompt. One config per request is recommended (each takes minutes on CPU).</summary>
    public static IEndpointRouteBuilder MapAssistantEval(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dev/assistant/eval", async (
            string? models, string? prompts, int? repeats, bool? verbose, string? label,
            IChatClient diChat, IOptions<AiOptions> ai, ToolLimits limits, IHostEnvironment env, CancellationToken ct) =>
        {
            var path = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "infra", "local", "eval", "assistant-eval.json"));
            if (!File.Exists(path)) return Results.NotFound($"Eval file not found: {path}");
            var set = JsonSerializer.Deserialize<AssistantEvalFile>(await File.ReadAllTextAsync(path, ct), JsonOpts)!;

            const StringSplitOptions split = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
            var modelList = (models ?? ai.Value.Chat.Model).Split(',', split);
            var promptList = (prompts ?? "current").Split(',', split);
            var reps = Math.Clamp(repeats ?? 1, 1, 5);
            var tools = new AssistantTools(null!, null!, null!, null!, limits, new AssistantRunState()).CreateTools();

            var summaries = new List<object>();
            var rows = new List<object>();

            foreach (var model in modelList)
            {
                foreach (var promptName in promptList)
                {
                    if (!Prompts.TryGetValue(promptName, out var system))
                    {
                        summaries.Add(new { model, prompt = promptName, error = "unknown prompt" });
                        continue;
                    }

                    HttpClient? http = null;
                    IChatClient client;
                    if (ai.Value.Provider == AiProvider.AzureOpenAI)
                    {
                        client = diChat;
                    }
                    else
                    {
                        http = new HttpClient
                        {
                            BaseAddress = new Uri(ai.Value.Ollama.Endpoint),
                            Timeout = TimeSpan.FromSeconds(ai.Value.Chat.TimeoutSeconds)
                        };
                        client = new OllamaSharp.OllamaApiClient(http, model);
                    }
                    using var _ = http;

                    var options = new ChatOptions
                    {
                        Temperature = 0f,
                        MaxOutputTokens = Math.Max(300, ai.Value.Query.MaxAnswerTokens),
                        AllowMultipleToolCalls = false,
                        Tools = tools
                    };

                    var verdicts = new Dictionary<string, int>();
                    var times = new List<long>();
                    long inputTokens = 0;
                    long outputTokensTotal = 0;
                    var passed = 0;
                    var total = 0;
                    string? unavailable = null;

                    foreach (var c in set.Cases)
                    {
                        if (unavailable is not null) break;
                        for (var rep = 1; rep <= reps; rep++)
                        {
                            var message = c.Message.Replace("{G}", FixedRedeemId).Replace("{O}", FixedOfferId);
                            var msgs = new List<ChatMessage>
                            {
                                new(ChatRole.System, system),
                                new(ChatRole.User, message)
                            };

                            var sw = Stopwatch.StartNew();
                            ChatResponse? resp = null;
                            string? filteredNote = null;
                            try
                            {
                                resp = await client.GetResponseAsync(msgs, options, ct);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception ex) when (ex.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase))
                            {
                                filteredNote = "blocked by AOAI content_filter";
                            }
                            catch (Exception ex)
                            {
                                unavailable = $"{ex.GetType().Name}: {ex.Message}";   // e.g. model not installed
                                break;
                            }
                            sw.Stop();

                            string verdict;
                            string detail;
                            if (filteredNote is not null)
                            {
                                verdict = c.NoToolOk ? "pass" : "filtered";
                                detail = filteredNote;
                            }
                            else
                            {
                                var calls = resp!.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
                                var text = resp.Text?.Trim() ?? "";
                                (verdict, detail) = Score(c, calls, text, resp.Usage?.OutputTokenCount);
                                if (resp.Usage?.InputTokenCount is { } it) inputTokens += it;
                                if (resp.Usage?.OutputTokenCount is { } ot) outputTokensTotal += ot;
                            }

                            total++;
                            if (verdict == "pass") passed++;
                            verdicts[verdict] = verdicts.GetValueOrDefault(verdict) + 1;
                            times.Add(sw.ElapsedMilliseconds);

                            if (verdict != "pass" || verbose == true)
                                rows.Add(new { model, prompt = promptName, c.Id, c.Category, verdict, detail, ms = sw.ElapsedMilliseconds });
                        }
                    }

                    times.Sort();
                    summaries.Add(new
                    {
                        model,
                        prompt = promptName,
                        unavailable,
                        passed,
                        total,
                        passRatePct = total == 0 ? 0 : Math.Round(100.0 * passed / total, 0),
                        verdicts,
                        avgMs = times.Count == 0 ? 0 : (long)times.Average(),
                        p95Ms = times.Count == 0 ? 0 : times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * 0.95) - 1)],
                        avgInputTokens = total == 0 ? 0 : inputTokens / total,
                        avgOutputTokens = total == 0 ? 0 : outputTokensTotal / total
                    });
                }
            }

            return Results.Ok(new { label, ranAtUtc = DateTime.UtcNow, repeats = reps, summaries, failures = rows });
        });

        return app;
    }

    private static (string Verdict, string Detail) Score(
        AssistantEvalCase c, IReadOnlyList<FunctionCallContent> calls, string text, long? outputTokens)
    {
        if (calls.Count == 0)
        {
            if (text.Length == 0) return ("empty", $"no tool call and no text (out={outputTokens})");
            return c.NoToolOk ? ("pass", "answered without a tool") : ("missed_tool", $"expected {string.Join("/", c.Allowed)}");
        }

        var first = calls[0];
        var extra = calls.Count > 1 ? $" (+{calls.Count - 1} more calls)" : "";

        if (c.Allowed.Length == 0) return ("unneeded_tool", $"{first.Name}{extra} {Describe(first)}");
        if (!c.Allowed.Contains(first.Name)) return ("wrong_tool", $"{first.Name}{extra}");

        if (c.ArgCheck is not null && !ArgOk(c.ArgCheck, first))
            return ("bad_arg", $"{first.Name} {Describe(first)}");

        return ("pass", $"{first.Name}{extra}");
    }

    private static string? Arg(FunctionCallContent f, string name) =>
        f.Arguments is not null && f.Arguments.TryGetValue(name, out var v) ? v?.ToString() : null;

    private static bool ArgOk(string check, FunctionCallContent f) => check switch
    {
        "redeemId" => string.Equals(Arg(f, "redeemId")?.Trim(), FixedRedeemId, StringComparison.OrdinalIgnoreCase),
        "offerId" => string.Equals(Arg(f, "offerId")?.Trim(), FixedOfferId, StringComparison.OrdinalIgnoreCase),
        "query" => Arg(f, "query") is { Length: > 0 and <= 200 },
        _ => true
    };

    private static string Describe(FunctionCallContent f) =>
        f.Arguments is null ? "" : string.Join(",", f.Arguments.Select(a => $"{a.Key}={Trunc(a.Value?.ToString())}"));

    private static string Trunc(string? s) => s is null ? "" : s.Length <= 40 ? s : s[..40] + "…";
}