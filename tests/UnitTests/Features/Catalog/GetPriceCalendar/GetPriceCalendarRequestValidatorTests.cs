using Catalog.Contracts;
using Catalog.Features.GetPriceCalendar;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
namespace UnitTests.Features.Catalog.GetPriceCalendar;

public class GetPriceCalendarRequestValidatorTests
{

    // Fixed instant: From is now bounded relative to "today" at both ends, so
    // these cases need a known one rather than the wall clock.
    private static readonly DateTimeOffset FixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(FixedInstant.UtcDateTime);

    private static readonly StaySearchPolicyOptions Policy = new StaySearchPolicyOptions();

    private readonly GetPriceCalendarRequestValidator _sut = new GetPriceCalendarRequestValidator(
        Options.Create(Policy), CreateTimeProvider());

    private static FakeTimeProvider CreateTimeProvider()
    {
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(FixedInstant);
        return timeProvider;
    }

    private static GetPriceCalendarRequest CreateValidRequest()
    {
        return new GetPriceCalendarRequest
        {
            UnitId = Guid.NewGuid(),
            From = Today,
            To = Today.AddDays(7)
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        GetPriceCalendarRequest request = CreateValidRequest();

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForUnitId_WhenEmpty()
    {
        GetPriceCalendarRequest request = CreateValidRequest() with { UnitId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.UnitId);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForTo_WhenNotAfterFrom()
    {
        GetPriceCalendarRequest request = CreateValidRequest() with { To = Today };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.To);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForTo_WhenBeforeFrom()
    {
        GetPriceCalendarRequest request = CreateValidRequest() with { To = Today.AddDays(-1) };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.To);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForRange_WhenSpanExceedsTheMaximum()
    {
        GetPriceCalendarRequest request = CreateValidRequest() with { From = Today, To = Today.AddDays(GetPriceCalendarRequestValidator.MaxRangeDays + 1) };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor("range");
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForRange_WhenSpanIsExactlyTheMaximum()
    {
        GetPriceCalendarRequest request = CreateValidRequest() with { From = Today, To = Today.AddDays(GetPriceCalendarRequestValidator.MaxRangeDays) };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor("range");
    }

    // The span cap above and the From bounds below are different guarantees,
    // which is how From came to be unbounded while the range was not. The
    // handler caches on a key containing From, on an anonymous unthrottled
    // endpoint, so an unbounded From is an unbounded set of cache entries -
    // each one paying for a generate_series cross join to populate.

    [Fact]
    public void Validate_ShouldHaveError_ForFrom_WhenFarInThePast()
    {
        // The case that made the key space ~3.6 million wide per unit: nothing
        // stopped From being any date DateOnly can represent.
        GetPriceCalendarRequest request = CreateValidRequest() with
        {
            From = new DateOnly(1873, 1, 1),
            To = new DateOnly(1873, 2, 1)
        };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.From);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForFrom_WhenBeyondTheBookableHorizon()
    {
        GetPriceCalendarRequest request = CreateValidRequest() with
        {
            From = Today.AddDays(Policy.MaxLeadTimeDays + 2),
            To = Today.AddDays(Policy.MaxLeadTimeDays + 3)
        };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.From);
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForFrom_AtTheEdgesOfTheAllowedWindow()
    {
        // Both ends stay open by a day past the nominal bound, because "today"
        // here is UTC while the hold path resolves it in the property's own
        // zone - so this endpoint never refuses to price a stay that could
        // still be held. A full window back also keeps a current-month
        // calendar grid working.
        GetPriceCalendarRequest earliest = CreateValidRequest() with
        {
            From = Today.AddDays(-GetPriceCalendarRequestValidator.MaxRangeDays),
            To = Today.AddDays(-GetPriceCalendarRequestValidator.MaxRangeDays + 1)
        };
        GetPriceCalendarRequest latest = CreateValidRequest() with
        {
            From = Today.AddDays(Policy.MaxLeadTimeDays + 1),
            To = Today.AddDays(Policy.MaxLeadTimeDays + 2)
        };

        _sut.TestValidate(earliest).ShouldNotHaveValidationErrorFor(x => x.From);
        _sut.TestValidate(latest).ShouldNotHaveValidationErrorFor(x => x.From);
    }
}
