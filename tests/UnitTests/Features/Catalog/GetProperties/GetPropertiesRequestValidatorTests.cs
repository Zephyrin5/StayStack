using Catalog.Contracts;
using Catalog.Features.GetProperties;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
namespace UnitTests.Features.Catalog.GetProperties;

public class GetPropertiesRequestValidatorTests
{

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    // The bounds are shared with the hold path now rather than being this
    // validator's own constants - the defaults are what appsettings ships.
    private static readonly StaySearchPolicyOptions Policy = new StaySearchPolicyOptions();
    private readonly GetPropertiesRequestValidator _sut =
        new GetPropertiesRequestValidator(Options.Create(Policy));

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
            CheckOut = Today.AddDays(Policy.MaxStayNights)
        };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.CheckOut);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCheckOut_WhenStayExceedsMaxNights()
    {
        // Without this, an anonymous caller could search a decades-wide
        // window - see GetPropertiesHandler's lead-time guard for
        // the other half of that same bound.
        GetPropertiesRequest request = CreateValidRequest() with
        {
            CheckOut = Today.AddDays(Policy.MaxStayNights + 1)
        };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.CheckOut);
    }
}
