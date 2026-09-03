using BuildingBlocks.Pagination;
using Catalog.Contracts;
using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Options;
namespace Catalog.Features.GetProperties;

public sealed class GetPropertiesRequestValidator : Validator<GetPropertiesRequest>
{
    // Both halves of the stay-window rule live here, rather than stay length
    // here and lead time in the handler. They bound one request between them
    // and a reader should find them together; splitting them also meant the
    // lead-time 400 surfaced from the handler as a thrown exception while its
    // sibling came back from request validation.
    //
    // The hold path still splits them, and has to: its lead-time check
    // resolves "today" in the property's own time zone (docs/adr/0018), which
    // needs the Unit loaded, so HoldAvailabilityHandler owns it. Search
    // resolves against UTC because it spans every property's zone at once, and
    // a clock is injectable where a loaded entity isn't.
    //
    // Without either bound an anonymous caller could search a decades-wide
    // window - which GetPropertiesHandler answers by asking Availability for
    // every unit blocked anywhere on the platform across that whole window.
    // Both numbers come from StaySearchPolicyOptions, the same instance the
    // hold path reads, so search cannot offer a stay HoldAvailability would
    // then refuse.
    public GetPropertiesRequestValidator(
        IOptions<StaySearchPolicyOptions> staySearchPolicy, TimeProvider timeProvider)
    {
        int maxStayNights = staySearchPolicy.Value.MaxStayNights;
        int maxLeadTimeDays = staySearchPolicy.Value.MaxLeadTimeDays;

        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationDefaults.MaxPageSize);

        RuleFor(x => x.Guests).GreaterThan(0).When(x => x.Guests is not null);

        // CheckIn/CheckOut are a pair - "available from some date onward,
        // no end" isn't a search a caller can mean, so requiring one
        // requires the other rather than silently ignoring a lone value.
        RuleFor(x => x.CheckOut)
            .NotNull().WithMessage("CheckOut is required when CheckIn is provided.")
            .When(x => x.CheckIn is not null);

        RuleFor(x => x.CheckIn)
            .NotNull().WithMessage("CheckIn is required when CheckOut is provided.")
            .When(x => x.CheckOut is not null);

        RuleFor(x => x.CheckOut)
            .GreaterThan(x => x.CheckIn!.Value)
            .When(x => x.CheckIn is not null && x.CheckOut is not null);

        RuleFor(x => x)
            .Must(x => x.CheckOut!.Value.DayNumber - x.CheckIn!.Value.DayNumber <= maxStayNights)
            .WithName(nameof(GetPropertiesRequest.CheckOut))
            .WithMessage($"Stay length cannot exceed {maxStayNights} nights.")
            .When(x => x.CheckIn is not null && x.CheckOut is not null);

        // A UTC business date, deliberately - not an oversight, and the only
        // one in src/ that decides anything. Everything else that compares a
        // date to "today" resolves it in the property's own time zone via
        // PropertyTimeZone (docs/adr/0018), because it has a property in hand
        // and the answer would otherwise be wrong by a day near midnight.
        // A search has no property in scope yet - it spans every zone at once,
        // so there is no zone to resolve against - and a day of fuzz on a
        // 730-day bound decides nothing. The one other UTC date, in
        // ListMyReviewableBookingsHandler, is a different case again: a query
        // bound widened a day either side on purpose, with the exact
        // per-zone test applied to the results afterwards.
        //
        // GetUtcNow() inside the rule, not hoisted into the constructor
        // alongside the two ints above. FastEndpoints resolves validators
        // once and reuses them, so a "today" captured here would be the day
        // the process started - drifting a day further out of date every day
        // the app stays up, and silently rejecting valid searches.
        RuleFor(x => x.CheckIn)
            .Must(checkIn => checkIn!.Value.DayNumber
                             - DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).DayNumber
                             <= maxLeadTimeDays)
            .WithMessage($"Check-in date cannot be more than {maxLeadTimeDays} days in the future.")
            .When(x => x.CheckIn is not null);
    }
}
