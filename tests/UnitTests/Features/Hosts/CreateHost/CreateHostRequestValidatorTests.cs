using FluentValidation.TestHelper;
using Hosts.Features.CreateHost;
namespace UnitTests.Features.Hosts.CreateHost;

public class CreateHostRequestValidatorTests
{
    private readonly CreateHostRequestValidator _sut = new CreateHostRequestValidator();

    private static CreateHostRequest CreateValidRequest()
    {
        return new CreateHostRequest
        {
            BusinessName = "Gulf Stays Co.",
            ContactEmail = "contact@gulfstays.example",
            ContactPhone = "+965 1234 5678",
            DisplayName = new Dictionary<string, string> { { "en", "Gulf Stays" } }
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        CreateHostRequest request = CreateValidRequest();

        TestValidationResult<CreateHostRequest> result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenDisplayNameIsNull()
    {
        // DisplayName's own content is validated by LocalizedText.Create's
        // guard clauses in the handler, not this validator - see its
        // constructor comment.
        CreateHostRequest request = CreateValidRequest() with { DisplayName = null };

        TestValidationResult<CreateHostRequest> result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForBusinessName_WhenEmpty()
    {
        CreateHostRequest request = CreateValidRequest() with { BusinessName = "" };

        TestValidationResult<CreateHostRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.BusinessName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Validate_ShouldHaveError_ForContactEmail_WhenInvalid(string email)
    {
        CreateHostRequest request = CreateValidRequest() with { ContactEmail = email };

        TestValidationResult<CreateHostRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.ContactEmail);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForContactPhone_WhenTooLong()
    {
        CreateHostRequest request = CreateValidRequest() with { ContactPhone = new string('1', 51) };

        TestValidationResult<CreateHostRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.ContactPhone);
    }
}
