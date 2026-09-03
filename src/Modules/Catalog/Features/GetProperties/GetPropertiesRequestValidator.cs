using BuildingBlocks.Pagination;
using Catalog.Contracts;
using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Options;
namespace Catalog.Features.GetProperties;

public sealed class GetPropertiesRequestValidator : Validator<GetPropertiesRequest>
{
    // Pure request-shape rule (doesn't need "today") - same split as the
    // hold path's: this bounds stay length, GetPropertiesHandler's lead-time
    // guard bounds how far out CheckIn can be (that one needs "today", so it
    // can't live here). Without either, an anonymous caller could search a
    // decades-wide window - which GetPropertiesHandler answers by asking
    // Availability for every unit blocked anywhere on the platform across
    // that whole window.
    //
    // Both numbers come from StaySearchPolicyOptions, the same instance the
    // hold path reads, so search cannot offer a stay that HoldAvailability
    // would then refuse.
    public GetPropertiesRequestValidator(IOptions<StaySearchPolicyOptions> staySearchPolicy)
    {
        int maxStayNights = staySearchPolicy.Value.MaxStayNights;

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
    }
}
