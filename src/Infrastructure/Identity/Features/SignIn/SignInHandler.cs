using BuildingBlocks.Exceptions;
using Identity.Entities;
using Identity.Features.Common;
using Mediator;
using Microsoft.AspNetCore.Identity;
namespace Identity.Features.SignIn;

public class SignInHandler(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IAuthTokenProvider authTokenProvider) : IRequestHandler<SignInRequest, SignInResponse>
{
    public async ValueTask<SignInResponse> Handle(SignInRequest request, CancellationToken cancellationToken)
    {
        // 1. Verify User Exists
        ApplicationUser? user = await userManager.FindByEmailAsync(request.Email);
        if (user == null) throw new InvalidCredentialsException();

        // 2. Authenticate the password safely without throwing AOT dynamic proxy errors
        SignInResult result = await signInManager.CheckPasswordSignInAsync(user, request.Password, false);
        if (!result.Succeeded) throw new InvalidCredentialsException();

        // 3. Retrieve user roles from database
        var roles = await userManager.GetRolesAsync(user);

        // 4. Generate the JWT Token (Using the AOT-safe JsonWebTokenHandler instead of JwtSecurityTokenHandler)
        string accessToken = authTokenProvider.GenerateJwtToken(user, roles);
        string refreshToken = await authTokenProvider.GenerateRefreshToken(user.Id, cancellationToken);

        // 5. Return our positional record using modern constructor syntax
        return new SignInResponse
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
