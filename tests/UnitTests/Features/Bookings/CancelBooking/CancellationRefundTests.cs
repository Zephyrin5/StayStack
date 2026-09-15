using Bookings.Features.CancelBooking;
using BuildingBlocks.Time;
using Microsoft.Extensions.Time.Testing;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace UnitTests.Features.Bookings.CancelBooking;

// The default policy: 100% five or more days out, 50% one to four, nothing inside a day.
public class CancellationRefundTests
{
    private static readonly Money Total = Money.Of(200m, Currency.KWD);
    private static readonly DateOnly CheckIn = new DateOnly(2026, 8, 25);

    [Theory]
    [InlineData(10, 200)]
    [InlineData(5, 200)]
    [InlineData(4, 100)]
    [InlineData(1, 100)]
    [InlineData(0, 0)]
    public void Compute_AppliesTheTierForTheDaysBeforeCheckIn(int daysBeforeCheckIn, int expected)
    {
        Money refund = CancellationRefund.Compute(Total, CancellationPolicy.CreateDefault(), CheckIn, CheckIn.AddDays(-daysBeforeCheckIn));

        Assert.Equal(Money.Of(expected, Currency.KWD), refund);
    }

    [Fact]
    public void Compute_AfterCheckIn_LandsOnTheStrictestTier()
    {
        Money refund = CancellationRefund.Compute(Total, CancellationPolicy.CreateDefault(), CheckIn, CheckIn.AddDays(3));

        Assert.Equal(Money.Of(0m, Currency.KWD), refund);
    }

    [Fact]
    public void Compute_WithThePropertyLocalDate_PaysTheTierTheGuestIsIn()
    {
        // 21:30 UTC on the 20th is the 21st in Kuwait: 4 days out (50%); a UTC date says 5 (100%).
        FakeTimeProvider clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 20, 21, 30, 0, TimeSpan.Zero));

        Money refund = CancellationRefund.Compute(
            Total, CancellationPolicy.CreateDefault(), CheckIn, PropertyTimeZone.Today(clock, "Asia/Kuwait"));

        Assert.Equal(Money.Of(100m, Currency.KWD), refund);
    }
}
