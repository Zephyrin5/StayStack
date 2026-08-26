using BuildingBlocks.Exceptions;
using Identity.Entities;
using Mediator;
using Microsoft.AspNetCore.Identity;
namespace Identity.Features.AssignRole;

public class AssignRoleHandler(UserManager<ApplicationUser> userManager) : IRequestHandler<AssignRoleRequest, AssignRoleResponse>
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

        IList<string> roles = await userManager.GetRolesAsync(user);
        return new AssignRoleResponse { UserId = user.Id, Roles = [.. roles] };
    }
}
