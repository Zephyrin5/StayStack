using Bookings.Features.ConfirmBooking;
using FluentValidation.TestHelper;
namespace UnitTests.Features.Bookings.ConfirmBooking;

public class ConfirmBookingRequestValidatorTests
{
    private readonly ConfirmBookingRequestValidator _sut = new ConfirmBookingRequestValidator();

    private static ConfirmBookingRequest CreateValidRequest()
    {
        return new ConfirmBookingRequest
        {
            HoldId = Guid.NewGuid(),
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com",
            GuestPhone = "+965 1234 5678"
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        ConfirmBookingRequest request = CreateValidRequest();

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForGuestPhone_WhenNull()
    {
        ConfirmBookingRequest request = CreateValidRequest() with { GuestPhone = null };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.GuestPhone);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForHoldId_WhenEmpty()
    {
        ConfirmBookingRequest request = CreateValidRequest() with { HoldId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.HoldId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_ShouldHaveError_ForGuestName_WhenEmpty(string guestName)
    {
        ConfirmBookingRequest request = CreateValidRequest() with { GuestName = guestName };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.GuestName);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForGuestName_WhenTooLong()
    {
        ConfirmBookingRequest request = CreateValidRequest() with { GuestName = new string('a', 201) };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.GuestName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Validate_ShouldHaveError_ForGuestEmail_WhenInvalid(string guestEmail)
    {
        ConfirmBookingRequest request = CreateValidRequest() with { GuestEmail = guestEmail };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.GuestEmail);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForGuestPhone_WhenTooLong()
    {
        ConfirmBookingRequest request = CreateValidRequest() with { GuestPhone = new string('1', 51) };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.GuestPhone);
    }
}
