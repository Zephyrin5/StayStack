using BuildingBlocks.Identity;
using FastEndpoints;
using Identity.Features.AssignRole;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Users;

public class AssignRoleEndpoint(IMediator mediator) : Endpoint<AssignRoleRequest, AssignRoleResponse>
{
    public override void Configure()
    {
        Post("{UserId}/roles/{Role}");
        Policies(AuthorizationPolicies.Administrator);
        Group<UsersGroup>();

        Summary(s =>
        {
            s.Summary = "Add a role to a user (admin-only)";
            s.Description = "Idempotent-ish: adding a role the user already has succeeds without effect. " +
                            "Returns the user's full updated role set so the caller doesn't need a refetch. " +
                            "The target user picks up the new role on their next token refresh/sign-in, not " +
                            "immediately - their current access token was already issued.";
            s.Response<AssignRoleResponse>(200, "Role added.");
            s.Response<ValidationProblemDetails>(400, "Validation failed, or the role name doesn't exist.");
            s.Response<ProblemDetails>(404, "User not found.");
        });
    }

    public override async Task HandleAsync(AssignRoleRequest req, CancellationToken ct)
    {
        AssignRoleResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
