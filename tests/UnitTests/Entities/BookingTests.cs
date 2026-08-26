using Bookings.Contracts;
using Bookings.Entities;
using SeedWork.Enums;
namespace UnitTests.Entities;

public class BookingTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static Booking CreateValidBooking(Guid? customerId = null)
    {
        return Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), customerId,
            "Jane Guest", "jane@example.com", "+965 1234 5678",
            Today, Today.AddDays(3), 2, 150m, Currency.KWD);
    }

    [Fact]
    public void Create_ShouldSetAllProperties_WhenInputIsValid()
    {
        Guid id = Guid.NewGuid();
        Guid unitId = Guid.NewGuid();
        Guid holdId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();

        Booking booking = Booking.Create(
            id, unitId, holdId, customerId,
            "Jane Guest", "jane@example.com", "+965 1234 5678",
            Today, Today.AddDays(3), 2, 150m, Currency.KWD);

        Assert.Equal(id, booking.Id);
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
        Assert.Equal(Currency.KWD, booking.Currency);
        Assert.Equal(BookingStatus.Pending, booking.BookingStatus);
    }

    [Fact]
    public void Create_ShouldAllowNullCustomerIdAndNullGuestPhone_ForGuestCheckout()
    {
        Booking booking = Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            "Jane Guest", "jane@example.com", null,
            Today, Today.AddDays(1), 1, 50m, Currency.KWD);

        Assert.Null(booking.CustomerId);
        Assert.Null(booking.GuestPhone);
    }

    [Fact]
    public void Create_ShouldThrow_WhenIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), 1, 50m, Currency.KWD));
    }

    [Fact]
    public void Create_ShouldThrow_WhenUnitIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), 1, 50m, Currency.KWD));
    }

    [Fact]
    public void Create_ShouldThrow_WhenHoldIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), 1, 50m, Currency.KWD));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldThrow_WhenGuestNameIsNullOrWhitespace(string? guestName)
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, guestName!, "jane@example.com", null, Today, Today.AddDays(1), 1, 50m, Currency.KWD));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Create_ShouldThrow_WhenGuestEmailIsInvalid(string? guestEmail)
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Jane", guestEmail!, null, Today, Today.AddDays(1), 1, 50m, Currency.KWD));
    }

    [Fact]
    public void Create_ShouldThrow_WhenCheckOutIsNotAfterCheckIn()
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today, 1, 50m, Currency.KWD));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_ShouldThrow_WhenGuestCountIsNotPositive(int guestCount)
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), guestCount, 50m, Currency.KWD));
    }

    [Fact]
    public void Create_ShouldThrow_WhenTotalPriceIsNegative()
    {
        Assert.ThrowsAny<ArgumentException>(() => Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), 1, -1m, Currency.KWD));
    }

    [Fact]
    public void Create_ShouldAllowZeroTotalPrice()
    {
        Booking booking = Booking.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Jane", "jane@example.com", null, Today, Today.AddDays(1), 1, 0m, Currency.KWD);

        Assert.Equal(0m, booking.TotalPrice);
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

    [Fact]
    public void Confirm_ShouldSetStatusToConfirmed()
    {
        Booking booking = CreateValidBooking();

        booking.Confirm();

        Assert.Equal(BookingStatus.Confirmed, booking.BookingStatus);
    }

    [Fact]
    public void Confirm_ShouldBeIdempotent_WhenCalledTwice()
    {
        Booking booking = CreateValidBooking();

        booking.Confirm();
        Exception? exception = Record.Exception(booking.Confirm);

        Assert.Null(exception);
        Assert.Equal(BookingStatus.Confirmed, booking.BookingStatus);
    }

    [Fact]
    public void Confirm_ShouldThrow_WhenBookingIsCancelled()
    {
        Booking booking = CreateValidBooking();
        booking.Cancel();

        Assert.Throws<BookingNotPayableException>(booking.Confirm);
    }
}
