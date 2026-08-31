using Identity.Entities;
using Identity.Exceptions;
using Identity.Features.Common;
using Mediator;
using Microsoft.AspNetCore.Identity;
namespace Identity.Features.SignIn;

public class SignInHandler(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IPasswordHasher<ApplicationUser> passwordHasher,
    IAuthTokenProvider authTokenProvider) : IRequestHandler<SignInRequest, SignInResponse>
{
    // Verified against this fixed dummy hash when the email doesn't exist,
    // so that branch pays the same hashing cost as a real wrong-password
    // attempt - otherwise the two failure paths are distinguishable by
    // response time alone, the exact enumeration InvalidCredentialsException
    // exists to prevent.
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "not-a-real-password");

    public async ValueTask<SignInResponse> Handle(SignInRequest request, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            passwordHasher.VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash, request.Password);
            throw new InvalidCredentialsException();
        }

        // lockoutOnFailure: true arms the lockout configured in
        // IdentityServicesRegistration - failed attempts count toward
        // MaxFailedAccessAttempts. IsLockedOut deliberately maps to the same
        // InvalidCredentialsException as any other failure: a distinct
        // response would itself be an enumeration oracle (only an existing
        // account can be locked out), undoing the timing-parity check
        // above. Accepted tradeoff: a locked-out user just sees "invalid
        // credentials" - docs/adr/0016.
        SignInResult result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded) throw new InvalidCredentialsException();

        var roles = await userManager.GetRolesAsync(user);

        string accessToken = authTokenProvider.GenerateJwtToken(user, roles);
        string refreshToken = await authTokenProvider.GenerateRefreshToken(user.Id, familyId: null, parentTokenId: null, cancellationToken);

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
