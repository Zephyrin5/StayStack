using Mediator;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace Catalog.Features.CreateUnit;

public record CreateUnitRequest : IRequest<CreateUnitResponse>
{
    public Guid PropertyId { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public int MaxOccupancy { get; init; }
    public decimal BasePrice { get; init; }
    public Currency Currency { get; init; } = Currency.KWD;

    // Optional - omitted entirely means "use the platform default"
    // (CancellationPolicy.CreateDefault(), the Moderate shape), same
    // reasoning a brand-new host who hasn't thought about cancellation
    // terms yet shouldn't have to.
    public List<CancellationTier>? CancellationTiers { get; init; }
}
