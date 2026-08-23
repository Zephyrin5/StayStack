using Mediator;
using SeedWork.Enums;
namespace Catalog.Features.CreateUnit;

public record CreateUnitRequest : IRequest<CreateUnitResponse>
{
    public Guid PropertyId { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public int MaxOccupancy { get; init; }
    public decimal BasePrice { get; init; }
    public Currency Currency { get; init; } = Currency.KWD;
}
