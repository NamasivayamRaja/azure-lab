using AzureLab.Assistant;

namespace AzureLabs.Api.Assistant;

public sealed record CallerContext(
    string UserId, Guid EventId, IReadOnlySet<Guid> AllowedEventIds, string CorrelationId) : ICallerContext;