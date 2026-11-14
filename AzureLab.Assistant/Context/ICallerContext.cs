namespace AzureLab.Assistant;

/// <summary>
/// Who is calling and which event the request is scoped to. Built by the API from the
/// authenticated principal, NEVER from model output or the request body.
/// </summary>
public interface ICallerContext
{
    string UserId { get; }
    Guid EventId { get; }                         // the one event this request is scoped to
    IReadOnlySet<Guid> AllowedEventIds { get; }   // from claims; checked again by every tool
    string CorrelationId { get; }
}

public static class CallerContextExtensions
{
    public static bool MayReadScopedEvent(this ICallerContext c) =>
        c.EventId != Guid.Empty && c.AllowedEventIds.Contains(c.EventId);
}