using Mediator;
namespace Catalog.Features.GetPropertyById;

public record GetPropertyByIdRequest : IRequest<GetPropertyByIdResponse>
{
    public Guid PropertyId { get; init; }
}
