using Mediator;
using SeedWork.Enums;
namespace Catalog.Features.GetProperties;

public record GetPropertiesRequest : IRequest<GetPropertiesResponse>
{
    public string? City { get; init; }
    public PropertyType? PropertyType { get; init; }
}
