using Mediator;
namespace Catalog.Features.DeleteProperty;

public record DeletePropertyRequest : IRequest<DeletePropertyResponse>
{
    public Guid PropertyId { get; init; }
}
