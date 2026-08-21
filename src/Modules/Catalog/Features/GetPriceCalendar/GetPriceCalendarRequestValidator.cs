using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.GetPriceCalendar;

public sealed class GetPriceCalendarRequestValidator : Validator<GetPriceCalendarRequest>
{
    public GetPriceCalendarRequestValidator()
    {
        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.To).GreaterThan(x => x.From);

        RuleFor(x => x)
            .Must(x => x.To.DayNumber - x.From.DayNumber <= 366)
            .WithMessage("Date range cannot exceed 366 days.")
            .WithName("range");
    }
}
