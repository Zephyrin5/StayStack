using BuildingBlocks.Identity;
using FastEndpoints;
using Identity.Features.RemoveRole;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Users;

public class RemoveRoleEndpoint(IMediator mediator) : Endpoint<RemoveRoleRequest, RemoveRoleResponse>
{
    public override void Configure()
    {
        Delete("{UserId}/roles/{Role}");
        Policies(AuthorizationPolicies.Administrator);
        Group<UsersGroup>();

        Summary(s =>
        {
            s.Summary = "Remove a role from a user (admin-only)";
            s.Description = "Rejected if this would leave zero Administrators. Returns the user's full " +
                            "updated role set.";
            s.Response<RemoveRoleResponse>(200, "Role removed.");
            s.Response<ValidationProblemDetails>(400, "Validation failed, or this would remove the last Administrator.");
            s.Response<ProblemDetails>(404, "User not found.");
        });
    }

    public override async Task HandleAsync(RemoveRoleRequest req, CancellationToken ct)
    {
        RemoveRoleResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
