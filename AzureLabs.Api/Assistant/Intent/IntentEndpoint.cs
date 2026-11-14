using System.Security.Claims;
using System.Text.RegularExpressions;
using AzureLab.Assistant;
using AzureLab.Rag;
using Microsoft.Extensions.Options;

namespace AzureLabs.Api.Assistant;

public sealed record IntentRequest(string? Message);
public sealed record IntentResponse(string Status, object? Intent, int Attempts, long TotalMs);

internal static partial class CallerGate
{
    public static (CallerContext? Caller, IResult? Error) Create(HttpContext http, ClaimsPrincipal user, Guid eventId)
    {
        if (eventId == Guid.Empty) return (null, Results.Problem(statusCode: 400, title: "eventId is required."));

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (null, Results.Problem(statusCode: 401, title: "Unauthenticated."));

        var allowed = user.FindAll("event_id")
            .Select(c => Guid.TryParse(c.Value, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();
        if (!allowed.Contains(eventId)) return (null, Results.Problem(statusCode: 403, title: "Not allowed for this event."));

        var supplied = http.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        var correlationId = supplied is { Length: > 0 and <= 64 } && SafeId().IsMatch(supplied)
            ? supplied : Guid.NewGuid().ToString();
        http.Response.Headers["X-Correlation-ID"] = correlationId;

        return (new CallerContext(userId, eventId, allowed, correlationId), null);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeId();
}

public static class IntentEndpoint
{
    public static IEndpointRouteBuilder MapIntentEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/events/{eventId:guid}/intent", HandleAsync)
           .RequireAuthorization()
           .WithName("EventIntent");
        return app;
    }

    private static async Task<IResult> HandleAsync(
        Guid eventId, IntentRequest request, HttpContext http, ClaimsPrincipal user,
        IntentExtractor extractor, IOptions<AiOptions> options, ILogger<IntentRequest> logger, CancellationToken ct)
    {
        var (caller, error) = CallerGate.Create(http, user, eventId);   // authorization before any model access
        if (error is not null) return error;

        var max = options.Value.Query.MaxQuestionChars;
        var message = request.Message?.Trim();
        if (string.IsNullOrEmpty(message)) return Results.Problem(statusCode: 400, title: "Message is required.");
        if (message.Length > max) return Results.Problem(statusCode: 400, title: $"Message is longer than {max} characters.");

        try
        {
            var r = await extractor.ExtractAsync(message, max, caller!.CorrelationId, ct);
            return r.Outcome == IntentOutcome.Rejected
                ? Results.Problem(statusCode: 422, title: "The request could not be understood safely.",
                                  detail: $"No action was taken. Attempts: {r.Attempts}.")
                : Results.Ok(new IntentResponse(r.Outcome == IntentOutcome.Unknown ? "unknown" : "ok",
                                                r.Intent, r.Attempts, r.TotalMs));
        }
        catch (AssistantUnavailableException ex)
        {
            logger.LogError(ex, "Intent extraction unavailable: {Reason}", ex.Message);
            http.Response.Headers.RetryAfter = "10";
            return Results.Problem(statusCode: 503, title: "The AI service is temporarily unavailable.");
        }
    }
}