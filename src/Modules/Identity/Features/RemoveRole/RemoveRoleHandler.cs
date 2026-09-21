using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Identity.Entities;
using Identity.Features.Common;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Hybrid;
namespace Identity.Features.RemoveRole;

public class RemoveRoleHandler(UserManager<ApplicationUser> userManager, HybridCache cache)
    : IRequestHandler<RemoveRoleRequest, RemoveRoleResponse>
{
    public async ValueTask<RemoveRoleResponse> Handle(RemoveRoleRequest request, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null)
        {
            throw new NotFoundException(nameof(ApplicationUser), request.UserId);
        }

        // The one invariant this endpoint can't let slip: removing the
        // Administrator role from the last remaining Administrator would
        // permanently lock every admin-only surface (including this one)
        // out of anyone's reach.
        if (string.Equals(request.Role, AuthorizationPolicies.Administrator, StringComparison.OrdinalIgnoreCase))
        {
            IList<ApplicationUser> administrators = await userManager.GetUsersInRoleAsync(AuthorizationPolicies.Administrator);
            if (administrators.Count <= 1 && administrators.Any(a => a.Id == user.Id))
            {
                throw new ValidationException(nameof(request.Role), "Cannot remove the last remaining Administrator.");
            }
        }

        IdentityResult result = await userManager.RemoveFromRoleAsync(user, request.Role);
        if (!result.Succeeded)
        {
            throw new ValidationException(nameof(request.Role), string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        // A removed role stays in every token the user already holds until the stamp moves, which
        // is the case this endpoint exists for (docs/adr/0030).
        await userManager.UpdateSecurityStampAsync(user);
        await SecurityStamps.InvalidateAsync(cache, user.Id, cancellationToken);

        IList<string> roles = await userManager.GetRolesAsync(user);
        return new RemoveRoleResponse { UserId = user.Id, Roles = [.. roles] };
    }
}
