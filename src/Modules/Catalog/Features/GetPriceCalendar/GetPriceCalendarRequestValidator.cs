using Catalog.Contracts;
using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Options;
namespace Catalog.Features.GetPriceCalendar;

public sealed class GetPriceCalendarRequestValidator : Validator<GetPriceCalendarRequest>
{
    /// <summary>
    ///     Longest window that can be asked for in one request, and equally
    ///     how far back <c>From</c> may sit. A year plus a day, so a caller
    ///     can ask for a full year across a leap boundary.
    /// </summary>
    public const int MaxRangeDays = 366;

    // The span was bounded here from the start; From itself was not, and the
    // two are not the same guarantee. GetPriceCalendarHandler caches on
    // "price-calendar:{UnitId}:{From}:{To}", so From is a cache key component
    // an anonymous, unthrottled caller chooses freely - and DateOnly spans
    // roughly 3.6 million days. Walking From forward a day at a time minted a
    // fresh entry every time, each holding up to MaxRangeDays of calendar and
    // each miss paying for a generate_series cross join, a pricing-rule load
    // and a cross-module availability call. Bounding the cache's size (see
    // ApiServicesRegistration) limits what that costs; bounding From is what
    // stops it being free to attempt.
    //
    // The bounds are the ones the endpoint's own answer is meaningful within:
    // nobody can book 1873, and nobody can book past the lead time the hold
    // path enforces. This collapses the key space from ~3.6M per unit to
    // MaxRangeDays + MaxLeadTimeDays + 2.
    public GetPriceCalendarRequestValidator(
        IOptions<StaySearchPolicyOptions> staySearchPolicy, TimeProvider timeProvider)
    {
        int maxLeadTimeDays = staySearchPolicy.Value.MaxLeadTimeDays;

        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.To).GreaterThan(x => x.From);

        RuleFor(x => x)
            .Must(x => x.To.DayNumber - x.From.DayNumber <= MaxRangeDays)
            .WithMessage($"Date range cannot exceed {MaxRangeDays} days.")
            .WithName("range");

        // UTC "today", for the same reason GetPropertiesRequestValidator uses
        // it and with the same one-day tolerance on each side. This endpoint
        // does have a unit in scope, so unlike search it could resolve the
        // property's own zone (docs/adr/0018) - but only by loading the unit,
        // which is a database round trip, and doing it in the handler would
        // put the check after the cache lookup it exists to protect. Both
        // bounds below are deliberately loose enough that a day of anchor
        // skew cannot reject a legitimate request.
        //
        // Read inside the rules rather than captured: FastEndpoints reuses
        // validator instances, so a hoisted "today" would freeze at startup.

        // Backwards: a full window, so a calendar grid showing the current
        // month - or a client whose local date is a day behind UTC - still
        // resolves. Past prices are a legitimate thing to render; unbounded
        // history is not.
        RuleFor(x => x.From)
            .Must(from => from.DayNumber
                          >= DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).DayNumber - MaxRangeDays)
            .WithMessage($"From cannot be more than {MaxRangeDays} days in the past.");

        // Forwards: the same horizon the hold path will accept, plus the day
        // of tolerance, so this endpoint never refuses to price a stay that
        // HoldAvailabilityHandler would go on to allow.
        RuleFor(x => x.From)
            .Must(from => from.DayNumber
                          - DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).DayNumber
                          <= maxLeadTimeDays + 1)
            .WithMessage($"From cannot be more than {maxLeadTimeDays} days in the future.");
    }
}
