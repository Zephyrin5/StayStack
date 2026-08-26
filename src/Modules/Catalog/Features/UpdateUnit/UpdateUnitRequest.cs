using Mediator;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace Catalog.Features.UpdateUnit;

public record UpdateUnitRequest : IRequest<UpdateUnitResponse>
{
    public Guid UnitId { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public int MaxOccupancy { get; init; }
    public decimal BasePrice { get; init; }
    public Currency Currency { get; init; } = Currency.KWD;

    // Required, unlike CreateUnitRequest's optional field - an existing
    // Unit always already has some policy, so an update always carries the
    // full current/edited value forward, same "replace everything
    // together" contract this request's other fields already have.
    public List<CancellationTier> CancellationTiers { get; init; } = [];
}
