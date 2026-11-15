using System.Security.Claims;
using AzureLabs.Api.Auth;

namespace AzureLabs.Api.Assistant;

public static class ProposalEndpoints
{
    public static IEndpointRouteBuilder MapProposalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/proposals/{proposalId:guid}/confirm", ConfirmAsync)
           .RequireAuthorization("ApiAccess")
           .WithName("ConfirmRedeemProposal");
        return app;
    }

    private static async Task<IResult> ConfirmAsync(
        Guid proposalId, HttpContext http, ClaimsPrincipal user,
        ProposalConfirmationService service, CancellationToken ct)
    {
        var userId = user.GetCallerObjectId();
        if (string.IsNullOrWhiteSpace(userId)) return Results.Problem(statusCode: 401, title: "Unauthenticated.");

        var r = await service.ConfirmAsync(proposalId, userId, ct);

        IResult result;
        switch (r.Status)
        {
            case ConfirmStatus.Confirmed:
                result = Results.Accepted(value: new { proposalId, redeemTransactionId = r.RedeemTransactionId, status = "confirmed", replayed = false });
                break;
            case ConfirmStatus.Replayed:
                result = Results.Ok(new { proposalId, redeemTransactionId = r.RedeemTransactionId, status = "confirmed", replayed = true });
                break;
            case ConfirmStatus.InProgress:
                http.Response.Headers.RetryAfter = "5";
                result = Results.Problem(statusCode: 409, title: "This proposal is being confirmed. Retry shortly.");
                break;
            case ConfirmStatus.Expired:
                result = Results.Problem(statusCode: 410, title: "The proposal has expired. Ask the assistant again.");
                break;
            case ConfirmStatus.Unavailable:
                http.Response.Headers.RetryAfter = "30";
                result = Results.Problem(statusCode: 503, title: "The redeem service is temporarily unavailable. Retry confirming.");
                break;
            default:
                result = Results.Problem(statusCode: 404, title: "Proposal not found.");
                break;
        }
        return result;
    }
}

