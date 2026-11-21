using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using AzureLab.Rag.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureLab.Rag.Providers.DocumentIntelligence;

public sealed class AzureDocumentIntelligenceExtractor(
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<AiOptions> options,
    ILogger<AzureDocumentIntelligenceExtractor> logger) : IDocumentTextExtractor
{
    private const string ApiVersion = "2024-11-30";
    private static readonly TokenRequestContext Scope = new(["https://cognitiveservices.azure.com/.default"]);

    private static readonly Dictionary<string, string> SupportedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg"
    };

    private static readonly HashSet<string> PermanentErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "InvalidContent",
        "InvalidContentLength",
        "InvalidRequest",
        "InvalidArgument",
        "InvalidImage",
        "InvalidPdf",
        "PasswordProtected",
        "UnsupportedMediaType"
    };

    public bool CanHandle(string extension) => SupportedMimeTypes.ContainsKey(extension);

    public async Task<DocumentExtractionResult> ExtractTextAsync(
        Stream content,
        string extension,
        CancellationToken ct = default)
    {
        if (!SupportedMimeTypes.TryGetValue(extension, out var mimeType))
            throw new PermanentIngestionException("UnsupportedExtension", $"Unsupported file extension '{extension}'.");

        var di = options.Value.DocumentIntelligence;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(di.TimeoutSeconds));

        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, timeoutCts.Token);
        var bytes = ms.ToArray();
        if (bytes.Length == 0)
            throw new PermanentIngestionException("EmptyDocument", "Document stream is empty.");

        try
        {
            var token = await credential.GetTokenAsync(Scope, timeoutCts.Token);
            var analyzeUri = new Uri(
                new Uri(di.Endpoint.TrimEnd('/') + "/"),
                $"documentintelligence/documentModels/{Uri.EscapeDataString(di.ModelId)}:analyze?api-version={ApiVersion}&outputContentFormat=markdown");

            using var submitReq = new HttpRequestMessage(HttpMethod.Post, analyzeUri)
            {
                Content = new ByteArrayContent(bytes)
            };
            submitReq.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            submitReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var submitResp = await httpClient.SendAsync(submitReq, timeoutCts.Token);
            if (submitResp.StatusCode != HttpStatusCode.Accepted)
            {
                var errBody = await submitResp.Content.ReadAsStringAsync(timeoutCts.Token);
                HandleErrorStatus(submitResp.StatusCode, submitResp.Headers.RetryAfter, errBody, "submit");
            }

            if (!submitResp.Headers.TryGetValues("Operation-Location", out var opLocations) ||
                opLocations.FirstOrDefault() is not { Length: > 0 } operationLocation)
            {
                throw new InvalidOperationException("Document Intelligence returned 202 Accepted without Operation-Location header.");
            }

            var pollDelay = GetRetryDelay(submitResp.Headers.RetryAfter, di.PollIntervalMs);
            return await PollForResultAsync(operationLocation, pollDelay, di.PollIntervalMs, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Transient timeout: escape so Service Bus retries rather than letting the 5-min auto-lock renewal expire.
            throw new TimeoutException($"Document Intelligence analysis timed out after {di.TimeoutSeconds}s.");
        }
    }

    private async Task<DocumentExtractionResult> PollForResultAsync(
        string operationLocation,
        TimeSpan initialDelay,
        int defaultPollMs,
        CancellationToken ct)
    {
        var delay = initialDelay;
        while (true)
        {
            await Task.Delay(delay, ct);

            var token = await credential.GetTokenAsync(Scope, ct);
            using var pollReq = new HttpRequestMessage(HttpMethod.Get, operationLocation);
            pollReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var pollResp = await httpClient.SendAsync(pollReq, ct);
            if (pollResp.StatusCode == HttpStatusCode.TooManyRequests || (int)pollResp.StatusCode >= 500)
            {
                delay = GetRetryDelay(pollResp.Headers.RetryAfter, defaultPollMs);
                logger.LogWarning("Document Intelligence poll returned {StatusCode}; retrying in {DelayMs}ms.",
                    (int)pollResp.StatusCode, (int)delay.TotalMilliseconds);
                continue;
            }

            if (!pollResp.IsSuccessStatusCode)
            {
                var errBody = await pollResp.Content.ReadAsStringAsync(ct);
                HandleErrorStatus(pollResp.StatusCode, pollResp.Headers.RetryAfter, errBody, "poll");
            }

            var result = await pollResp.Content.ReadFromJsonAsync<AnalyzeOperationResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("Document Intelligence returned empty poll body.");

            if (string.Equals(result.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                var text = result.AnalyzeResult?.Content ?? "";
                var pages = result.AnalyzeResult?.Pages?.Count ?? (string.IsNullOrWhiteSpace(text) ? 0 : 1);
                return new DocumentExtractionResult(text, pages);
            }

            if (string.Equals(result.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var code = result.Error?.Code ?? "UnknownError";
                var msg = result.Error?.Message ?? "Document Intelligence analysis failed.";
                if (PermanentErrorCodes.Contains(code) ||
                    (result.Error?.InnerError?.Code is { } innerCode && PermanentErrorCodes.Contains(innerCode)))
                {
                    throw new PermanentIngestionException("CorruptDocument", $"Document Intelligence rejected file ({code}): {msg}");
                }

                // Unknown / backend error: treat as transient so Service Bus retries.
                throw new InvalidOperationException($"Document Intelligence transient failure ({code}): {msg}");
            }

            delay = GetRetryDelay(pollResp.Headers.RetryAfter, defaultPollMs);
        }
    }

    private void HandleErrorStatus(HttpStatusCode status, RetryConditionHeaderValue? retryAfter, string body, string stage)
    {
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity)
        {
            throw new PermanentIngestionException("CorruptDocument",
                $"Document Intelligence {stage} returned HTTP {(int)status}: {Truncate(body)}");
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            var retryHint = retryAfter?.Delta?.TotalSeconds.ToString("F0") ?? retryAfter?.Date?.ToString("O") ?? "none";
            logger.LogWarning("Document Intelligence {Stage} throttled (429, Retry-After={RetryAfter}).", stage, retryHint);
        }

        throw new HttpRequestException(
            $"Document Intelligence {stage} failed with HTTP {(int)status}: {Truncate(body)}",
            inner: null,
            statusCode: status);
    }

    private static TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, int defaultMs)
    {
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(Math.Clamp(delta.TotalMilliseconds, 250, 10_000));
        return TimeSpan.FromMilliseconds(defaultMs);
    }

    private static string Truncate(string s) => s.Length <= 512 ? s : s[..512];

    private sealed record AnalyzeOperationResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("error")] AnalyzeError? Error,
        [property: JsonPropertyName("analyzeResult")] AnalyzeResultPayload? AnalyzeResult);

    private sealed record AnalyzeError(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("innererror")] AnalyzeInnerError? InnerError);

    private sealed record AnalyzeInnerError(
        [property: JsonPropertyName("code")] string? Code);

    private sealed record AnalyzeResultPayload(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("pages")] List<AnalyzePage>? Pages);

    private sealed record AnalyzePage(
        [property: JsonPropertyName("pageNumber")] int PageNumber);
}
