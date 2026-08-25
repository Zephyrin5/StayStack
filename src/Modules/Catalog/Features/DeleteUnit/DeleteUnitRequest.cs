using Mediator;
namespace Catalog.Features.DeleteUnit;

public record DeleteUnitRequest : IRequest<DeleteUnitResponse>
{
    public Guid UnitId { get; init; }
}
