using BuildingBlocks.Pagination;
using Identity.Entities;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Identity.Features.GetUsers;

public class GetUsersHandler(AppIdentityDbContext dbContext) : IRequestHandler<GetUsersRequest, PagedResponse<UserSummary>>
{
    public async ValueTask<PagedResponse<UserSummary>> Handle(GetUsersRequest request, CancellationToken cancellationToken)
    {
        IQueryable<ApplicationUser> query = dbContext.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            query = query.Where(u => dbContext.UserRoles
                .Join(dbContext.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
                .Any(x => x.UserId == u.Id && x.Name == request.Role));
        }

        // Id as a tiebreaker, not deliberate sort criteria - see docs/adr/0008.
        (List<ApplicationUser> users, int totalCount) = await query
            .OrderBy(u => u.Id)
            .ToPagedListAsync(request.Page, request.PageSize, cancellationToken);

        List<Guid> userIds = [.. users.Select(u => u.Id)];

        // One batched join for this page's role sets, not one
        // UserManager.GetRolesAsync call per row - same "one round trip,
        // not one per item" reasoning GetHostBookingsHandler already uses
        // for its own unit lookup.
        var roleRows = await dbContext.UserRoles
            .Where(ur => userIds.Contains(ur.UserId))
            .Join(dbContext.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .ToListAsync(cancellationToken);

        ILookup<Guid, string> rolesByUser = roleRows.ToLookup(x => x.UserId, x => x.Name ?? string.Empty);

        return new PagedResponse<UserSummary>
        {
            Items =
            [
                .. users.Select(u => new UserSummary
                {
                    UserId = u.Id,
                    Email = u.Email ?? string.Empty,
                    Roles = [.. rolesByUser[u.Id]],
                    HostId = u.HostId
                })
            ],
            Page = request.Page,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }
}
