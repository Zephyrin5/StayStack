using Catalog.Features.GetProperties;
using FluentValidation.TestHelper;
namespace UnitTests.Features.Catalog.GetProperties;

public class GetPropertiesRequestValidatorTests
{

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private readonly GetPropertiesRequestValidator _sut = new GetPropertiesRequestValidator();

    private static GetPropertiesRequest CreateValidRequest()
    {
        return new GetPropertiesRequest
        {
            CheckIn = Today,
            CheckOut = Today.AddDays(3)
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        GetPropertiesRequest request = CreateValidRequest();

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldNotHaveError_WhenNeitherDateIsProvided()
    {
        GetPropertiesRequest request = new GetPropertiesRequest();

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.CheckOut);
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForCheckOut_WhenStayIsExactlyMaxNights()
    {
        GetPropertiesRequest request = CreateValidRequest() with
        {
            CheckOut = Today.AddDays(GetPropertiesRequestValidator.MaxStayNights)
        };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.CheckOut);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCheckOut_WhenStayExceedsMaxNights()
    {
        // Without this, an anonymous caller could search a decades-wide
        // window - see GetPropertiesHandler's own MaxLeadTimeDays guard for
        // the other half of that same bound.
        GetPropertiesRequest request = CreateValidRequest() with
        {
            CheckOut = Today.AddDays(GetPropertiesRequestValidator.MaxStayNights + 1)
        };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.CheckOut);
    }
}
