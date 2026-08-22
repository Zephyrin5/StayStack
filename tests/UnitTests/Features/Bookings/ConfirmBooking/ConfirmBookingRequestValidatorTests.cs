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
            GuestPhone = "+965 1234 5678",
            GuestCount = 2
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        ConfirmBookingRequest request = CreateValidRequest();

        TestValidationResult<ConfirmBookingRequest> result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForGuestPhone_WhenNull()
    {
        ConfirmBookingRequest request = CreateValidRequest() with { GuestPhone = null };

        TestValidationResult<ConfirmBookingRequest> result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.GuestPhone);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForHoldId_WhenEmpty()
    {
        ConfirmBookingRequest request = CreateValidRequest() with { HoldId = Guid.Empty };

        TestValidationResult<ConfirmBookingRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.HoldId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_ShouldHaveError_ForGuestName_WhenEmpty(string guestName)
    {
        ConfirmBookingRequest request = CreateValidRequest() with { GuestName = guestName };

        TestValidationResult<ConfirmBookingRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.GuestName);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForGuestName_WhenTooLong()
    {
        ConfirmBookingRequest request = CreateValidRequest() with { GuestName = new string('a', 201) };

        TestValidationResult<ConfirmBookingRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.GuestName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Validate_ShouldHaveError_ForGuestEmail_WhenInvalid(string guestEmail)
    {
        ConfirmBookingRequest request = CreateValidRequest() with { GuestEmail = guestEmail };

        TestValidationResult<ConfirmBookingRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.GuestEmail);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForGuestPhone_WhenTooLong()
    {
        ConfirmBookingRequest request = CreateValidRequest() with { GuestPhone = new string('1', 51) };

        TestValidationResult<ConfirmBookingRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.GuestPhone);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldHaveError_ForGuestCount_WhenNotPositive(int guestCount)
    {
        ConfirmBookingRequest request = CreateValidRequest() with { GuestCount = guestCount };

        TestValidationResult<ConfirmBookingRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.GuestCount);
    }
}
