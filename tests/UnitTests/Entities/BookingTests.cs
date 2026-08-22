using Bookings.Entities;
namespace UnitTests.Entities;

public class BookingTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static Booking CreateValidBooking(Guid? customerId = null)
    {
        return Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), customerId,
            "Jane Guest", "jane@example.com", "+965 1234 5678",
            Today, Today.AddDays(3), 2, 150m, "KWD");
    }

    [Fact]
    public void Create_ShouldSetAllProperties_WhenInputIsValid()
    {
        Guid unitId = Guid.NewGuid();
        Guid holdId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();

        Booking booking = Booking.Create(
            unitId, holdId, customerId,
            "Jane Guest", "jane@example.com", "+965 1234 5678",
            Today, Today.AddDays(3), 2, 150m, "KWD");

        Assert.NotEqual(Guid.Empty, booking.Id);
        Assert.Equal(unitId, booking.UnitId);
        Assert.Equal(holdId, booking.HoldId);
        Assert.Equal(customerId, booking.CustomerId);
        Assert.Equal("Jane Guest", booking.GuestName);
        Assert.Equal("jane@example.com", booking.GuestEmail);
        Assert.Equal("+965 1234 5678", booking.GuestPhone);
        Assert.Equal(Today, booking.CheckIn);
        Assert.Equal(Today.AddDays(3), booking.CheckOut);
        Assert.Equal(2, booking.GuestCount);
        Assert.Equal(150m, booking.TotalPrice);
        Assert.Equal("KWD", booking.Currency);
        Assert.Equal(BookingStatus.Pending, booking.BookingStatus);
    }

    [Fact]
    public void Create_ShouldAllowNullCustomerIdAndNullGuestPhone_ForGuestCheckout()
    {
        Booking booking = Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), null,
            "Jane Guest", "jane@example.com", null,
            Today, Today.AddDays(1), 1, 50m, "KWD");

        Assert.Null(booking.CustomerId);
        Assert.Null(booking.GuestPhone);
    }

    [Fact]
    public void Create_ShouldThrow_WhenUnitIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.Empty, Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), 1, 50m, "KWD"));
    }

    [Fact]
    public void Create_ShouldThrow_WhenHoldIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.Empty, null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), 1, 50m, "KWD"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldThrow_WhenGuestNameIsNullOrWhitespace(string? guestName)
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, guestName!, "jane@example.com", null, Today, Today.AddDays(1), 1, 50m, "KWD"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Create_ShouldThrow_WhenGuestEmailIsInvalid(string? guestEmail)
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, "Jane", guestEmail!, null, Today, Today.AddDays(1), 1, 50m, "KWD"));
    }

    [Fact]
    public void Create_ShouldThrow_WhenCheckOutIsNotAfterCheckIn()
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today, 1, 50m, "KWD"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_ShouldThrow_WhenGuestCountIsNotPositive(int guestCount)
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), guestCount, 50m, "KWD"));
    }

    [Fact]
    public void Create_ShouldThrow_WhenTotalPriceIsNegative()
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), 1, -1m, "KWD"));
    }

    [Fact]
    public void Create_ShouldAllowZeroTotalPrice()
    {
        Booking booking = Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), 1, 0m, "KWD");

        Assert.Equal(0m, booking.TotalPrice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldThrow_WhenCurrencyIsNullOrWhitespace(string? currency)
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), 1, 50m, currency!));
    }

    [Fact]
    public void Cancel_ShouldSetStatusToCancelled()
    {
        Booking booking = CreateValidBooking();

        booking.Cancel();

        Assert.Equal(BookingStatus.Cancelled, booking.BookingStatus);
    }

    [Fact]
    public void Cancel_ShouldBeIdempotent_WhenCalledTwice()
    {
        Booking booking = CreateValidBooking();

        booking.Cancel();
        Exception? exception = Record.Exception(booking.Cancel);

        Assert.Null(exception);
        Assert.Equal(BookingStatus.Cancelled, booking.BookingStatus);
    }
}
