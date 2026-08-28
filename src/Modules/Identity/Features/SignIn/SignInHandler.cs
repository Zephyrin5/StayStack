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
    // Verified once against a fixed dummy user/password below when the
    // email doesn't exist, so that branch pays the same password-hashing
    // cost as a real wrong-password attempt instead of returning early -
    // otherwise the two failure paths are distinguishable by response time
    // alone, which is exactly the enumeration InvalidCredentialsException's
    // own doc comment says must not happen.
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "not-a-real-password");

    public async ValueTask<SignInResponse> Handle(SignInRequest request, CancellationToken cancellationToken)
    {
        // 1. Verify User Exists
        ApplicationUser? user = await userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            passwordHasher.VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash, request.Password);
            throw new InvalidCredentialsException();
        }

        // 2. Authenticate the password safely without throwing AOT dynamic
        // proxy errors. lockoutOnFailure: true arms the lockout configured
        // in IdentityServicesRegistration - failed attempts now count
        // toward MaxFailedAccessAttempts. IsLockedOut deliberately maps to
        // the same InvalidCredentialsException as any other failure below:
        // a distinct response would itself be an enumeration oracle (only
        // an account that exists can ever be locked out), which would undo
        // the timing-parity check above. The cost is a locked-out user
        // seeing a generic "invalid credentials" message for the lockout
        // window rather than being told why - an accepted tradeoff, see
        // docs/adr/0016.
        SignInResult result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded) throw new InvalidCredentialsException();

        // 3. Retrieve user roles from database
        var roles = await userManager.GetRolesAsync(user);

        // 4. Generate the JWT Token (Using the AOT-safe JsonWebTokenHandler instead of JwtSecurityTokenHandler)
        string accessToken = authTokenProvider.GenerateJwtToken(user, roles);
        string refreshToken = await authTokenProvider.GenerateRefreshToken(user.Id, familyId: null, parentTokenId: null, cancellationToken);

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
