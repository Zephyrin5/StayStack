using FluentValidation.TestHelper;
using Identity.Features.RefreshToken;
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

    // No "should error when empty/null" case - RefreshToken is optional at
    // the DTO level now (cookie-mode callers send no body, see
    // RefreshTokenRequest's own comment), so there's no NotEmpty rule to
    // assert. That case is 401, not 400 - covered by
    // RefreshTokenTests.ShouldReturn401_WhenTokenIsEmptyAndNoCookiePresent.
    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenTokenIsNullOrEmpty()
    {
        RefreshTokenRequest request = new RefreshTokenRequest { RefreshToken = null };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
