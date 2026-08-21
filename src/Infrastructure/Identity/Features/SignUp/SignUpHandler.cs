using BuildingBlocks.Exceptions;
using Identity.Entities;
using Identity.Features.Auth.Common;
using Identity.Features.SignUp;
using Mediator;
using Microsoft.AspNetCore.Identity;
namespace Identity.Features.Auth.SignUp;

public class SignUpHandler(
    UserManager<ApplicationUser> userManager,
    IAuthTokenProvider authTokenProvider) : IRequestHandler<SignUpRequest, SignUpResponse>
{
    // Matches the literal Name seeded in RoleConfiguration - every
    // self-registered account starts here. Becoming a Host is a separate,
    // later action on an existing account (see chat notes), not a
    // registration-time choice - Airbnb's own model does the same.
    private const string CustomerRoleName = "Customer";

    public async ValueTask<SignUpResponse> Handle(SignUpRequest request, CancellationToken cancellationToken)
    {
        ApplicationUser? existingUser = await userManager.FindByEmailAsync(request.Email);
        if (existingUser is not null)
        {
            throw new EmailAlreadyInUseException();
        }

        ApplicationUser user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email
        };

        IdentityResult createResult = await userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            // Identity's own password-policy/format errors are already
            // safe, specific, user-facing messages (unlike sign-in, where
            // vague-on-purpose is the right call) - surface them directly
            // rather than wrapping in a generic message.
            throw new ValidationException(
                nameof(request.Password),
                string.Join(" ", createResult.Errors.Select(e => e.Description)));
        }

        IdentityResult roleResult;
        try
        {
            roleResult = await userManager.AddToRoleAsync(user, CustomerRoleName);
        }
        catch (InvalidOperationException ex)
        {
            // Same reasoning as BecomeHostHandler's identical try/catch:
            // UserManager.AddToRoleAsync throws rather than returning a
            // failed IdentityResult when the role itself doesn't exist,
            // which is the realistic way this fails (seed data drift) -
            // normalized here so it reaches the ValidationException below
            // instead of surfacing as an unhandled 500.
            roleResult = IdentityResult.Failed(new IdentityError { Description = ex.Message });
        }

        if (!roleResult.Succeeded)
        {
            // Extremely unlikely once the seed data is in place, but if the
            // Customer role is ever missing, fail loudly rather than
            // leaving a roleless account behind silently.
            throw new ValidationException(
                "Role",
                string.Join(" ", roleResult.Errors.Select(e => e.Description)));
        }

        var roles = await userManager.GetRolesAsync(user);

        string accessToken = authTokenProvider.GenerateJwtToken(user, roles);
        string refreshToken = await authTokenProvider.GenerateRefreshToken(user.Id, cancellationToken);

        return new SignUpResponse
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            Roles = [.. roles]
        };
    }
}
