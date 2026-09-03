using Catalog.Contracts;
using Catalog.Features.GetProperties;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
namespace UnitTests.Features.Catalog.GetProperties;

public class GetPropertiesRequestValidatorTests
{
    // Fixed instant, so the lead-time rule below is asserted against a known
    // "today" rather than the wall clock. That rule became testable here at
    // all only once it moved out of GetPropertiesHandler and into the
    // validator alongside its other half.
    private static readonly DateTimeOffset FixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(FixedInstant.UtcDateTime);

    // The bounds are shared with the hold path now rather than being this
    // validator's own constants - the defaults are what appsettings ships.
    private static readonly StaySearchPolicyOptions Policy = new StaySearchPolicyOptions();

    private readonly GetPropertiesRequestValidator _sut = new GetPropertiesRequestValidator(
        Options.Create(Policy), CreateTimeProvider());

    private static FakeTimeProvider CreateTimeProvider()
    {
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(FixedInstant);
        return timeProvider;
    }

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
        // window - see the lead-time rule below for the other half of that
        // same bound.
        GetPropertiesRequest request = CreateValidRequest() with
        {
            CheckOut = Today.AddDays(Policy.MaxStayNights + 1)
        };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.CheckOut);
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForCheckIn_WhenLeadTimeIsExactlyTheMaximum()
    {
        GetPropertiesRequest request = CreateValidRequest() with
        {
            CheckIn = Today.AddDays(Policy.MaxLeadTimeDays),
            CheckOut = Today.AddDays(Policy.MaxLeadTimeDays + 1)
        };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.CheckIn);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCheckIn_WhenLeadTimeExceedsTheMaximum()
    {
        // CheckOut is one day after CheckIn, so the stay-length rule stays
        // satisfied and only the lead-time rule can be what fails here.
        GetPropertiesRequest request = CreateValidRequest() with
        {
            CheckIn = Today.AddDays(Policy.MaxLeadTimeDays + 1),
            CheckOut = Today.AddDays(Policy.MaxLeadTimeDays + 2)
        };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.CheckIn);
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForCheckIn_WhenNoDatesAreProvided()
    {
        // The lead-time rule is conditional on CheckIn being present - a
        // city-only search must not trip it.
        GetPropertiesRequest request = new GetPropertiesRequest { City = "Kuwait City" };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.CheckIn);
    }
}
