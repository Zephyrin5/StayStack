using FluentValidation.TestHelper;
using Identity.Features.BecomeHost;
namespace UnitTests.Features.BecomeHost;

public class BecomeHostRequestValidatorTests
{
    private readonly BecomeHostRequestValidator _sut = new BecomeHostRequestValidator();

    private static BecomeHostRequest CreateValidRequest()
    {
        return new BecomeHostRequest
        {
            BusinessName = "Gulf Stays Co.",
            ContactEmail = "contact@gulfstays.example",
            ContactPhone = "+965 1234 5678"
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        BecomeHostRequest request = CreateValidRequest();

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForContactPhone_WhenNull()
    {
        // ContactPhone is optional - only its MaximumLength is enforced,
        // there's no NotEmpty rule on it.
        BecomeHostRequest request = CreateValidRequest() with { ContactPhone = null };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.ContactPhone);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForBusinessName_WhenEmpty()
    {
        BecomeHostRequest request = CreateValidRequest() with { BusinessName = "" };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.BusinessName);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForBusinessName_WhenTooLong()
    {
        BecomeHostRequest request = CreateValidRequest() with { BusinessName = new string('a', 201) };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.BusinessName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Validate_ShouldHaveError_ForContactEmail_WhenInvalid(string email)
    {
        BecomeHostRequest request = CreateValidRequest() with { ContactEmail = email };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.ContactEmail);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForContactPhone_WhenTooLong()
    {
        BecomeHostRequest request = CreateValidRequest() with { ContactPhone = new string('1', 51) };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.ContactPhone);
    }
}
