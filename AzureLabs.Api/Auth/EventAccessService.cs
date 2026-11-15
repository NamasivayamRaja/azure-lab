using System.Security.Claims;
using AzureLab.Data;
using Microsoft.EntityFrameworkCore;

namespace AzureLabs.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public static string? GetCallerObjectId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ObjectIdClaim)
        ?? user.FindFirstValue("oid")
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static bool IsServiceCaller(this ClaimsPrincipal user) =>
        user.IsInRole("Api.Service") || user.HasClaim("roles", "Api.Service");
}

public interface IEventAccessService
{
    Task<bool> CanAccessEventAsync(ClaimsPrincipal user, Guid eventId, CancellationToken ct);
    Task<IReadOnlySet<Guid>> GetAllowedEventIdsAsync(ClaimsPrincipal user, CancellationToken ct);
}

public sealed class EfEventAccessService(AppDbContext db) : IEventAccessService
{
    public async Task<bool> CanAccessEventAsync(ClaimsPrincipal user, Guid eventId, CancellationToken ct)
    {
        if (eventId == Guid.Empty) return false;
        if (user.IsServiceCaller()) return true;

        var userId = user.GetCallerObjectId();
        if (string.IsNullOrWhiteSpace(userId)) return false;

        return await db.EventEnrollments.AsNoTracking()
            .AnyAsync(e => e.EventId == eventId && e.UserId == userId, ct);
    }

    public async Task<IReadOnlySet<Guid>> GetAllowedEventIdsAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.GetCallerObjectId();
        if (string.IsNullOrWhiteSpace(userId)) return new HashSet<Guid>();

        var ids = await db.EventEnrollments.AsNoTracking()
            .Where(e => e.UserId == userId)
            .Select(e => e.EventId)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }
}
