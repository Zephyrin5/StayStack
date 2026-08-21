using Mediator;
namespace Catalog.Features.GetPriceCalendar;

public record GetPriceCalendarRequest : IRequest<GetPriceCalendarResponse>
{
    public Guid UnitId { get; init; }
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
}
