using System.Security.Claims;
using System.Text.RegularExpressions;
using AzureLab.Assistant;
using AzureLab.Rag;
using AzureLabs.Api.Auth;
using Microsoft.Extensions.Options;

namespace AzureLabs.Api.Assistant;

public sealed record AssistantRequest(string? Message);
public sealed record AssistantResponse(string Outcome, string Answer, IReadOnlyList<ToolCallRecord> ToolCalls, IReadOnlyList<RedeemProposalView> Proposals);

public static partial class AssistantEndpoint
{
    public static IEndpointRouteBuilder MapAssistantEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/events/{eventId:guid}/assistant", HandleAsync)
           .RequireAuthorization("ApiAccess")
           .WithName("EventAssistant");
        return app;
    }

    private static async Task<IResult> HandleAsync(
        Guid eventId, AssistantRequest request, HttpContext http, ClaimsPrincipal user,
        AssistantService assistant, IEventAccessService eventAccess, IOptions<AiOptions> options, ILogger<AssistantRequest> logger, CancellationToken ct)
    {
        var max = options.Value.Query.MaxQuestionChars;
        var message = request.Message?.Trim();
        if (eventId == Guid.Empty) return Results.Problem(statusCode: 400, title: "eventId is required.");
        if (string.IsNullOrEmpty(message)) return Results.Problem(statusCode: 400, title: "Message is required.");
        if (message.Length > max) return Results.Problem(statusCode: 400, title: $"Message is longer than {max} characters.");

        var userId = user.GetCallerObjectId();
        if (string.IsNullOrWhiteSpace(userId)) return Results.Problem(statusCode: 401, title: "Unauthenticated.");

        // Authorization BEFORE any model or data access. The event comes from the route, the ACL from EventEnrollments.
        if (!await eventAccess.CanAccessEventAsync(user, eventId, ct))
            return Results.Problem(statusCode: 403, title: "Not allowed for this event.");
        var allowed = await eventAccess.GetAllowedEventIdsAsync(user, ct);

        var supplied = http.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        var correlationId = supplied is { Length: > 0 and <= 64 } && SafeId().IsMatch(supplied)
            ? supplied : Guid.NewGuid().ToString();
        http.Response.Headers["X-Correlation-ID"] = correlationId;

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["EventId"] = eventId
        });

        var caller = new CallerContext(userId, eventId, allowed, correlationId);
        try
        {
            var result = await assistant.AskAsync(caller, message, ct);
            if (result.Outcome == "InternalError")
                return Results.Problem(statusCode: 500, title: "The assistant failed unexpectedly.");   // a bug must not look like an answer
            // Dev only: the tool trace is returned so we can inspect behaviour. Not a production response shape.
            return Results.Ok(new AssistantResponse(result.Outcome, result.Answer, result.ToolCalls, result.Proposals));
        }
        catch (AssistantUnavailableException ex)
        {
            logger.LogError(ex, "Assistant unavailable: {Reason}", ex.Message);
            http.Response.Headers.RetryAfter = "10";
            return Results.Problem(statusCode: 503, title: "The AI service is temporarily unavailable.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeId();
}