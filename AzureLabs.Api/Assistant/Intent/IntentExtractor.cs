using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AzureLab.Assistant;

public enum IntentOutcome { Ok, Unknown, Rejected }

public sealed record IntentResult(IntentOutcome Outcome, ValidatedIntent? Intent, int Attempts, string? LastError, long TotalMs);

public sealed class IntentExtractor(IChatClient chat, ILoggerFactory loggerFactory)
{
    public const int MaxAttempts = 2;          // 1 initial + 1 repair
    private const int MaxOutputTokens = 200;   // the JSON is tiny; FinishReason=Length is treated as a failure

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static readonly ChatResponseFormat Format = ChatResponseFormat.ForJsonSchema<ExtractedIntent>(
        JsonOptions, "ExtractedIntent", "The classified intent of the user's message");

    private const string SystemPrompt = """
        Classify the user's message into ONE intent and return a JSON object that matches the schema.
        Intents:
        - CheckRedeemStatus: the user asks about the status of a redeem request and wrote its id.
        - AskEventQuestion: the user asks a factual question about the event (times, rules, places).
        - ListDocuments: the user asks which documents exist for the event.
        - RequestRedeem: the user asks to redeem an offer and wrote the offer id.
        - Unknown: anything else, or a required id is missing.
        Rules:
        - Copy ids exactly as written in the message. Never invent or complete an id.
        - If a needed id is not in the message, use the intent Unknown.
        - Fields that do not belong to the intent must be empty strings.
        - For AskEventQuestion, put the user's question in "question".
        """;

    private readonly ILogger _log = loggerFactory.CreateLogger<IntentExtractor>();

    public async Task<IntentResult> ExtractAsync(string message, int maxQuestionChars, string correlationId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var options = new ChatOptions { Temperature = 0f, MaxOutputTokens = MaxOutputTokens, ResponseFormat = Format };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, message)
        };

        string? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ChatResponse response;
            try
            {
                response = await chat.GetResponseAsync(messages, options, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AssistantUnavailableException("model_timeout");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new AssistantUnavailableException("model_rate_limited", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or Polly.ExecutionRejectedException)
            {
                throw new AssistantUnavailableException("model_unavailable", ex);
            }

            var text = response.Text?.Trim() ?? "";
            ExtractedIntent? parsed = null;
            string? error = null;

            if (response.FinishReason == ChatFinishReason.Length)
                error = "The output was cut off. Return only the short JSON object.";
            else if (text.Length == 0)
                error = "The output was empty. Return the JSON object.";
            else
            {
                try { parsed = JsonSerializer.Deserialize<ExtractedIntent>(text, JsonOptions); }
                catch (JsonException) { error = "The output was not valid JSON for the schema."; }
            }

            if (error is null)
            {
                var v = IntentValidator.Validate(parsed, message, maxQuestionChars);
                if (v.IsValid)
                {
                    var outcome = v.Intent is UnknownIntent ? IntentOutcome.Unknown : IntentOutcome.Ok;
                    _log.LogInformation(
                        "intent_extracted outcome={Outcome} kind={Kind} attempts={Attempts} totalMs={Ms} messageLength={Len} correlation={CorrelationId}",
                        outcome, v.Intent!.Kind, attempt, sw.ElapsedMilliseconds, message.Length, correlationId);
                    return new IntentResult(outcome, v.Intent, attempt, null, sw.ElapsedMilliseconds);
                }
                error = v.Error;
            }

            lastError = error;
            _log.LogWarning(
                "intent_invalid attempt={Attempt} reason={Reason} outputChars={Chars} finish={Finish} correlation={CorrelationId}",
                attempt, error, text.Length, response.FinishReason, correlationId);

            // Repair turn: the NEW information is our fixed validation message. An identical retry at temperature 0 would repeat itself.
            if (text.Length > 0) messages.Add(new ChatMessage(ChatRole.Assistant, text));
            messages.Add(new ChatMessage(ChatRole.User, $"Your previous output was rejected: {error} Return the corrected JSON object only."));
        }

        _log.LogWarning("intent_rejected attempts={Attempts} lastReason={Reason} totalMs={Ms} correlation={CorrelationId}",
            MaxAttempts, lastError, sw.ElapsedMilliseconds, correlationId);
        return new IntentResult(IntentOutcome.Rejected, null, MaxAttempts, lastError, sw.ElapsedMilliseconds);
    }
}