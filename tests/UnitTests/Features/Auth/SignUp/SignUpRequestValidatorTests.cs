using FluentValidation.TestHelper;
using Identity.Features.SignUp;
namespace UnitTests.Features.Auth.SignUp;

public class SignUpRequestValidatorTests
{
    private readonly SignUpRequestValidator _sut = new SignUpRequestValidator();

    private static SignUpRequest CreateValidRequest()
    {
        return new SignUpRequest
        {
            Email = "user@example.com",
            Password = "correct-horse-battery-staple",
            ConfirmPassword = "correct-horse-battery-staple"
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        SignUpRequest request = CreateValidRequest();

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Validate_ShouldHaveError_ForEmail_WhenInvalid(string email)
    {
        SignUpRequest request = CreateValidRequest() with { Email = email };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")] // exactly 11 characters - one short of the MinimumLength(12) rule
    public void Validate_ShouldHaveError_ForPassword_WhenShorterThanTwelveCharacters(string password)
    {
        SignUpRequest request = CreateValidRequest() with { Password = password, ConfirmPassword = password };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForConfirmPassword_WhenItDoesNotMatchPassword()
    {
        SignUpRequest request = CreateValidRequest() with { ConfirmPassword = "a-different-password-entirely" };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.ConfirmPassword);
    }
}
