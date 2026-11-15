using System.Data.Common;
using AzureLab.Rag;
using AzureLab.Rag.Query;
using AzureLabs.Api.Auth;
using Microsoft.Extensions.Options;

namespace AzureLabs.Api.Ask;

public sealed record AskRequest(string Question);

public static class AskEndpoint
{
    public static IEndpointRouteBuilder MapAskEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/events/{eventId:guid}/ask", HandleAsync)
           .RequireAuthorization("ApiAccess")
           .WithName("AskEventQuestion")
           .Produces<AskResult>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> HandleAsync(
        Guid eventId,
        AskRequest? body,
        IOptions<AiOptions> options,
        EventQuestionService service,
        IEventAccessService eventAccess,
        IHostEnvironment env,
        HttpContext http,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // A2: Fast-fail with 503 when Ai:Enabled is false (e.g., Cloud Week 1 before Azure OpenAI / AI Search is provisioned)
        if (!options.Value.Enabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The AI service is disabled in this environment.");
        }

        var logger = loggerFactory.CreateLogger("AskEndpoint");
        var maxChars = options.Value.Query.MaxQuestionChars;

        if (eventId == Guid.Empty)
            return Results.Problem(statusCode: 400, title: "eventId must not be empty.");

        var question = body?.Question?.Trim();
        if (string.IsNullOrWhiteSpace(question))
            return Results.Problem(statusCode: 400, title: "question is required.");

        if (question.Length > maxChars)
            return Results.Problem(statusCode: 400, title: $"question must be at most {maxChars} characters.");

        var correlationId = http.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        http.Response.Headers["X-Correlation-ID"] = correlationId;
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["EventId"] = eventId
        });

        // Per-event authorization BEFORE any retrieval or model call.
        if (!await eventAccess.CanAccessEventAsync(http.User, eventId, ct))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed for this event.");

        try
        {
            var result = await service.AskAsync(eventId, question, ct);
            // Diagnostics help calibration locally; they are not part of a production response.
            return Results.Ok(env.IsDevelopment() ? result : result with { Diagnostics = null });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is HttpRequestException or TimeoutException or TaskCanceledException or DbException or Polly.ExecutionRejectedException)
        {
            logger.LogError(ex, "Ask failed: model or vector store unavailable ({ExceptionType})", ex.GetType().Name);
            http.Response.Headers.RetryAfter = "10";
            return Results.Problem(statusCode: 503, title: "The AI service is temporarily unavailable.");
        }
    }
}
