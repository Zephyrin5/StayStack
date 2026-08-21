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

        TestValidationResult<RefreshTokenRequest> result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_WhenTokenIsEmpty()
    {
        RefreshTokenRequest request = new RefreshTokenRequest { RefreshToken = "" };

        TestValidationResult<RefreshTokenRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.RefreshToken);
    }
}
