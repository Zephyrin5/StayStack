using Availability.Features.HoldAvailability;
using Catalog.Contracts;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
namespace UnitTests.Features.Availability.HoldAvailability;

public class HoldAvailabilityRequestValidatorTests
{

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    // Shared with the search path rather than defined here - see
    // StaySearchPolicyOptions. The defaults are what appsettings ships.
    private static readonly StaySearchPolicyOptions Policy = new StaySearchPolicyOptions();
    private readonly HoldAvailabilityRequestValidator _sut =
        new HoldAvailabilityRequestValidator(Options.Create(Policy));

    private static HoldAvailabilityRequest CreateValidRequest()
    {
        return new HoldAvailabilityRequest
        {
            UnitId = Guid.NewGuid(),
            CheckIn = Today,
            CheckOut = Today.AddDays(3),
            GuestCount = 2
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        HoldAvailabilityRequest request = CreateValidRequest();

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForUnitId_WhenEmpty()
    {
        HoldAvailabilityRequest request = CreateValidRequest() with { UnitId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.UnitId);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCheckOut_WhenNotAfterCheckIn()
    {
        HoldAvailabilityRequest request = CreateValidRequest() with { CheckOut = Today };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.CheckOut);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldHaveError_ForGuestCount_WhenNotPositive(int guestCount)
    {
        HoldAvailabilityRequest request = CreateValidRequest() with { GuestCount = guestCount };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.GuestCount);
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForCheckOut_WhenStayIsExactlyMaxNights()
    {
        HoldAvailabilityRequest request = CreateValidRequest() with
        {
            CheckOut = Today.AddDays(Policy.MaxStayNights)
        };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.CheckOut);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCheckOut_WhenStayExceedsMaxNights()
    {
        // Without this, an anonymous caller could hold a unit for a decade
        // in one request - see HoldAvailabilityHandler's lead-time
        // guard for the other half of that same bound.
        HoldAvailabilityRequest request = CreateValidRequest() with
        {
            CheckOut = Today.AddDays(Policy.MaxStayNights + 1)
        };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.CheckOut);
    }
}
