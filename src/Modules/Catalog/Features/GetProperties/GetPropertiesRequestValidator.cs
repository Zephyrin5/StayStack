using BuildingBlocks.Pagination;
using Catalog.Contracts;
using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Options;
namespace Catalog.Features.GetProperties;

public sealed class GetPropertiesRequestValidator : Validator<GetPropertiesRequest>
{
    // Both halves of the stay-window rule are here rather than one here and one in the handler: they
    // bound one request between them, and split, the lead-time 400 arrived as a thrown exception
    // while its sibling came back from validation. The hold path has to split them - its check needs
    // the property's time zone, and so the loaded Unit.
    //
    // They do different jobs. Stay length is what stops an anonymous caller searching a decades-wide
    // window; lead time is a product rule about how far ahead this platform sells, and moves where
    // the window sits rather than how wide it is. Both numbers come from StaySearchPolicyOptions, the
    // instance the hold path reads.
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

        // A UTC business date, deliberately: a search spans every property's zone at once, so there
        // is no zone to resolve against (docs/adr/0018 covers the rest of src, which has a property
        // in hand and must not use UTC).
        //
        // + 1 because the anchor cannot be shared with the hold path, which uses the property's zone.
        // Near the boundary the two disagree by a day, and the direction depends on an offset search
        // cannot know: east of UTC a strict search would hide a property the hold would still accept -
        // inventory missing from results with nothing to say why. A day of slack makes search never
        // stricter than the hold, leaving only the loud failure, and a day is exactly enough because a
        // local date is within one day of the UTC date in every zone.
        //
        // The message still quotes maxLeadTimeDays: 730 is the product rule, and the extra day is
        // tolerance for an anchor the caller cannot see.
        //
        // GetUtcNow() inside the rule, not hoisted: FastEndpoints reuses a validator, so a "today"
        // captured in the constructor would be the day the process started.
        RuleFor(x => x.CheckIn)
            .Must(checkIn => checkIn!.Value.DayNumber
                             - DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).DayNumber
                             <= maxLeadTimeDays + 1)
            .WithMessage($"Check-in date cannot be more than {maxLeadTimeDays} days in the future.")
            .When(x => x.CheckIn is not null);
    }
}
