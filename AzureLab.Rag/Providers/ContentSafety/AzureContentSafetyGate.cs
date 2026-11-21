using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using AzureLab.Rag.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureLab.Rag.Providers.ContentSafety;

internal sealed class AzureContentSafetyGate(
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<AiOptions> options,
    ILogger<AzureContentSafetyGate> logger) : IContentSafetyGate
{
    private const string ApiVersion = "2024-09-01";
    private static readonly TokenRequestContext Scope = new(["https://cognitiveservices.azure.com/.default"]);
    private static readonly string[] Categories = ["Hate", "SelfHarm", "Sexual", "Violence"];

    public async Task<PromptShieldDecision> ShieldAsync(
        string? userPrompt,
        IReadOnlyList<string> documents,
        CancellationToken ct = default)
    {
        var cs = options.Value.ContentSafety;
        if (!cs.Enabled)
            return PromptShieldDecision.Skipped();

        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(cs.TimeoutSeconds));

        try
        {
            var token = await credential.GetTokenAsync(Scope, timeoutCts.Token);
            var uri = new Uri(new Uri(cs.Endpoint.TrimEnd('/') + "/"), $"contentsafety/text:shieldPrompt?api-version={ApiVersion}");

            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(new ShieldPromptRequest(userPrompt ?? string.Empty, documents))
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var resp = await httpClient.SendAsync(req, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
                logger.LogWarning("Content Safety shieldPrompt returned {StatusCode}: {Body}", (int)resp.StatusCode, body);
                return new PromptShieldDecision(false, false, Array.Empty<int>(), sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode}");
            }

            var payload = await resp.Content.ReadFromJsonAsync<ShieldPromptResponse>(cancellationToken: timeoutCts.Token);
            var userAttack = payload?.UserPromptAnalysis?.AttackDetected == true;
            var attackedDocs = new List<int>();
            if (payload?.DocumentsAnalysis is not null)
            {
                for (var i = 0; i < payload.DocumentsAnalysis.Count; i++)
                {
                    if (payload.DocumentsAnalysis[i].AttackDetected)
                        attackedDocs.Add(i);
                }
            }

            return new PromptShieldDecision(true, userAttack, attackedDocs, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Content Safety shieldPrompt timed out after {TimeoutSeconds}s.", cs.TimeoutSeconds);
            return new PromptShieldDecision(false, false, Array.Empty<int>(), sw.ElapsedMilliseconds, "Timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Content Safety shieldPrompt failed.");
            return new PromptShieldDecision(false, false, Array.Empty<int>(), sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    public async Task<ModerationDecision> ModerateTextAsync(
        string text,
        CancellationToken ct = default)
    {
        var cs = options.Value.ContentSafety;
        if (!cs.Enabled || string.IsNullOrWhiteSpace(text))
            return ModerationDecision.Skipped();

        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(cs.TimeoutSeconds));

        try
        {
            var token = await credential.GetTokenAsync(Scope, timeoutCts.Token);
            var uri = new Uri(new Uri(cs.Endpoint.TrimEnd('/') + "/"), $"contentsafety/text:analyze?api-version={ApiVersion}");

            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(new AnalyzeTextRequest(text, Categories, "FourSeverityLevels"))
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var resp = await httpClient.SendAsync(req, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
                logger.LogWarning("Content Safety text:analyze returned {StatusCode}: {Body}", (int)resp.StatusCode, body);
                return new ModerationDecision(false, false, new Dictionary<string, int>(), sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode}");
            }

            var payload = await resp.Content.ReadFromJsonAsync<AnalyzeTextResponse>(cancellationToken: timeoutCts.Token);
            var severities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var blocked = false;
            if (payload?.CategoriesAnalysis is not null)
            {
                foreach (var item in payload.CategoriesAnalysis)
                {
                    severities[item.Category] = item.Severity;
                    if (item.Severity >= cs.SeverityThreshold)
                        blocked = true;
                }
            }

            return new ModerationDecision(true, blocked, severities, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Content Safety text:analyze timed out after {TimeoutSeconds}s.", cs.TimeoutSeconds);
            return new ModerationDecision(false, false, new Dictionary<string, int>(), sw.ElapsedMilliseconds, "Timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Content Safety text:analyze failed.");
            return new ModerationDecision(false, false, new Dictionary<string, int>(), sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    private sealed record ShieldPromptRequest(
        [property: JsonPropertyName("userPrompt")] string UserPrompt,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents);

    private sealed record ShieldPromptResponse(
        [property: JsonPropertyName("userPromptAnalysis")] AttackAnalysis? UserPromptAnalysis,
        [property: JsonPropertyName("documentsAnalysis")] List<AttackAnalysis>? DocumentsAnalysis);

    private sealed record AttackAnalysis(
        [property: JsonPropertyName("attackDetected")] bool AttackDetected);

    private sealed record AnalyzeTextRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("categories")] string[] Categories,
        [property: JsonPropertyName("outputType")] string OutputType);

    private sealed record AnalyzeTextResponse(
        [property: JsonPropertyName("categoriesAnalysis")] List<CategoryAnalysis>? CategoriesAnalysis);

    private sealed record CategoryAnalysis(
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("severity")] int Severity);
}
