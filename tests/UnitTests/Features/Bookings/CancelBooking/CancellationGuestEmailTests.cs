using Bookings.Features.CancelBooking;
namespace UnitTests.Features.Bookings.CancelBooking;

public class CancellationGuestEmailTests
{
    [Theory]
    [InlineData("jane@example.com", "jane@example.com")]
    [InlineData("  Jane@Example.COM ", "jane@example.com")]
    [InlineData("jane@example.com", " JANE@example.com ")]
    public void Matches_IgnoringCaseAndSurroundingWhitespace(string supplied, string booked) =>
        Assert.True(CancellationGuestEmail.Matches(supplied, booked));

    [Theory]
    [InlineData(null, "jane@example.com")]
    [InlineData("", "jane@example.com")]
    [InlineData("   ", "jane@example.com")]
    [InlineData("john@example.com", "jane@example.com")]
    [InlineData("jane@example.com", null)]
    public void DoesNotMatch_MissingOrDifferentAddresses(string? supplied, string? booked) =>
        Assert.False(CancellationGuestEmail.Matches(supplied, booked));
}
