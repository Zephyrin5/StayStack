using BuildingBlocks.Time;
using Microsoft.Extensions.Time.Testing;
namespace UnitTests.Time;

// The heart of docs/adr/0018: a business date is only meaningful relative to
// a place. Every case below is pinned to an instant where UTC and the
// property's own zone disagree about what day it is - the situation the old
// UTC-everywhere logic silently got wrong.
public class PropertyTimeZoneTests
{
    // 21:30 UTC on the 20th is 00:30 on the 21st in Kuwait (UTC+3).
    private static readonly DateTimeOffset LateEveningUtc =
        new DateTimeOffset(2026, 8, 20, 21, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Today_EastOfUtc_IsAlreadyTheNextDay()
    {
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(LateEveningUtc);

        Assert.Equal(new DateOnly(2026, 8, 21), PropertyTimeZone.Today(timeProvider, "Asia/Kuwait"));

        // What the old code would have produced - kept explicit so the
        // difference this whole change turns on is visible in one place.
        Assert.Equal(new DateOnly(2026, 8, 20), DateOnly.FromDateTime(LateEveningUtc.UtcDateTime));
    }

    [Fact]
    public void Today_WestOfUtc_IsStillThePreviousDay()
    {
        // 02:30 UTC on the 21st is 22:30 on the 20th in Toronto - the Toronto
        // guest whose same-day booking used to be rejected.
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 21, 2, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 8, 20), PropertyTimeZone.Today(timeProvider, "America/Toronto"));
    }

    [Fact]
    public void ToLocalDate_ResolvesARecordedInstant_NotJustNow()
    {
        // What CancelBookingHandler's recancel path needs: the date a
        // cancellation actually happened on, in the property's zone.
        Assert.Equal(
            new DateOnly(2026, 8, 21),
            PropertyTimeZone.ToLocalDate(LateEveningUtc, "Asia/Kuwait"));
    }

    [Fact]
    public void ToLocalDate_IsUnambiguousAcrossADstTransition()
    {
        // Converting an instant to a local date is always well-defined, even
        // inside a DST fold - the ambiguity runs the other way (a wall-clock
        // time that occurs twice or never). This pins that assumption, since
        // the whole design relies on it.
        //
        // 05:30 UTC on 2026-11-01 is 01:30 in Toronto, an hour that occurs
        // twice that morning. Both instants still land on the same date.
        DateOnly beforeFold = PropertyTimeZone.ToLocalDate(
            new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), "America/Toronto");
        DateOnly afterFold = PropertyTimeZone.ToLocalDate(
            new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), "America/Toronto");

        Assert.Equal(new DateOnly(2026, 11, 1), beforeFold);
        Assert.Equal(new DateOnly(2026, 11, 1), afterFold);
    }

    [Theory]
    [InlineData("Asia/Kuwait")]
    [InlineData("America/Toronto")]
    [InlineData("Europe/London")]
    [InlineData("UTC")]
    public void IsValid_AcceptsRealZones(string timeZoneId) =>
        Assert.True(PropertyTimeZone.IsValid(timeZoneId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not/AZone")]
    [InlineData("Asia/Kuwait ")]
    public void IsValid_RejectsAnythingElse(string? timeZoneId) =>
        Assert.False(PropertyTimeZone.IsValid(timeZoneId));

    [Fact]
    public void Today_Throws_RatherThanFallingBackToUtc()
    {
        // The rule the whole ADR rests on: at read time an unusable zone is
        // an error, never a guess. Falling back to UTC here would reinstate
        // exactly the skew being removed - and under this app's UTC+3 market,
        // in the permissive, money-losing direction.
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(LateEveningUtc);

        Assert.Throws<InvalidOperationException>(
            () => PropertyTimeZone.Today(timeProvider, "Not/AZone"));
    }

    [Fact]
    public void SystemHasRealTimeZoneData()
    {
        // Canary. Setting InvariantGlobalization=true (or losing tzdata from
        // a published image) makes every lookup above fail, which would
        // otherwise surface as an opaque 500 on cancellation rather than a
        // build-time signal. Neither is set today - src/Directory.Build.props
        // has only IsAotCompatible, and CI's AOT step is continue-on-error
        // analysis - so this guards against someone enabling either later.
        Assert.True(TimeZoneInfo.TryFindSystemTimeZoneById("America/Toronto", out TimeZoneInfo? toronto));
        Assert.NotNull(toronto);

        // A zone that actually observes DST, so an invariant-globalization
        // build (where everything silently resolves to UTC) fails here too.
        Assert.NotEqual(
            toronto.GetUtcOffset(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)),
            toronto.GetUtcOffset(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)));
    }
}
