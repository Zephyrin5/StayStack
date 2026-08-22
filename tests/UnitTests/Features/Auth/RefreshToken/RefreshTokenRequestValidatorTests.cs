using FluentValidation.TestHelper;
using Identity.Features.Auth.RefreshToken;
namespace UnitTests.Features.Auth.RefreshToken;

public class RefreshTokenRequestValidatorTests
{
    private readonly RefreshTokenRequestValidator _sut = new RefreshTokenRequestValidator();

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenTokenIsNonEmpty()
    {
        RefreshTokenRequest request = new RefreshTokenRequest { RefreshToken = "some-opaque-token-value" };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    // No "should have error when empty/null" case - RefreshToken is
    // optional at the DTO level now (cookie-mode callers send no body at
    // all, see RefreshTokenRequest's own comment), and there's no longer
    // a NotEmpty rule to assert. RefreshTokenEndpoint resolves the
    // body-or-cookie fallback itself; see its and RefreshTokenTests.cs's
    // "ShouldReturn401_WhenTokenIsEmptyAndNoCookiePresent" for where that
    // case is actually covered now (401, not a 400 validation failure).
    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenTokenIsNullOrEmpty()
    {
        RefreshTokenRequest request = new RefreshTokenRequest { RefreshToken = null };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
