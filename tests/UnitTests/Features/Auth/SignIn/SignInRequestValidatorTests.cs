using FluentValidation.TestHelper;
using Identity.Features.SignIn;
namespace UnitTests.Features.Auth.SignIn;

public class SignInRequestValidatorTests
{
    private readonly SignInRequestValidator _sut = new SignInRequestValidator();

    private static SignInRequest CreateValidRequest()
    {
        return new SignInRequest { Email = "user@example.com", Password = "whatever-they-typed" };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        SignInRequest request = CreateValidRequest();

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Validate_ShouldHaveError_ForEmail_WhenInvalid(string email)
    {
        SignInRequest request = CreateValidRequest() with { Email = email };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPassword_WhenEmpty()
    {
        // No MinimumLength here on purpose - unlike SignUp, this is a login
        // attempt, not setting a new password, so the rule really is just
        // "was something submitted", not "is it a policy-compliant password".
        SignInRequest request = CreateValidRequest() with { Password = "" };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Password);
    }
}
