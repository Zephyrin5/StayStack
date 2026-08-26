using BuildingBlocks.Identity;
using BuildingBlocks.Pagination;
using FastEndpoints;
using Identity.Features.GetUsers;
using Mediator;
namespace Api.Endpoints.Users;

public class GetUsersEndpoint(IMediator mediator) : Endpoint<GetUsersRequest, PagedResponse<UserSummary>>
{
    public override void Configure()
    {
        Get("");
        Policies(AuthorizationPolicies.Administrator);
        Group<UsersGroup>();

        Summary(s =>
        {
            s.Summary = "List users, optionally filtered by role (admin-only)";
            s.Description = "Paginated - defaults to page 1, 20 per page. Role is an exact match against " +
                            "role name (e.g. \"Host\", \"Administrator\").";
            s.Response<PagedResponse<UserSummary>>(200, "Users returned.");
        });
    }

    public override async Task HandleAsync(GetUsersRequest req, CancellationToken ct)
    {
        PagedResponse<UserSummary> result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
