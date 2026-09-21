using BuildingBlocks.Exceptions;
using Identity.Entities;
using Identity.Features.Common;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Hybrid;
namespace Identity.Features.AssignRole;

public class AssignRoleHandler(UserManager<ApplicationUser> userManager, HybridCache cache)
    : IRequestHandler<AssignRoleRequest, AssignRoleResponse>
{
    public async ValueTask<AssignRoleResponse> Handle(AssignRoleRequest request, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null)
        {
            throw new NotFoundException(nameof(ApplicationUser), request.UserId);
        }

        IdentityResult result;
        try
        {
            result = await userManager.AddToRoleAsync(user, request.Role);
        }
        catch (InvalidOperationException ex)
        {
            // Same normalization BecomeHostHandler already relies on -
            // AddToRoleAsync throws rather than returning a failed
            // IdentityResult when the role name itself doesn't exist.
            result = IdentityResult.Failed(new IdentityError { Description = ex.Message });
        }

        if (!result.Succeeded)
        {
            throw new ValidationException(nameof(request.Role), string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        // The new role is in the database and in no token the user is holding. Moving the stamp is
        // what makes those tokens stop, and the eviction is what makes it happen now rather than
        // within SecurityStamps.CacheWindow (docs/adr/0030).
        await userManager.UpdateSecurityStampAsync(user);
        await SecurityStamps.InvalidateAsync(cache, user.Id, cancellationToken);

        IList<string> roles = await userManager.GetRolesAsync(user);
        return new AssignRoleResponse { UserId = user.Id, Roles = [.. roles] };
    }
}
