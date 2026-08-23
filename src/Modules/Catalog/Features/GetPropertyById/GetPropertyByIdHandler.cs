using BuildingBlocks.Exceptions;
using Catalog.Entities;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Features.GetPropertyById;

public class GetPropertyByIdHandler(AppCatalogDbContext dbContext) : IRequestHandler<GetPropertyByIdRequest, GetPropertyByIdResponse>
{
    public async ValueTask<GetPropertyByIdResponse> Handle(GetPropertyByIdRequest request, CancellationToken cancellationToken)
    {
        Property property = await dbContext.Properties.AsNoTracking()
                                .SingleOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken)
                            ?? throw new NotFoundException(nameof(Property), request.PropertyId);

        var units = await dbContext.Units.AsNoTracking()
            .Where(u => u.PropertyId == request.PropertyId)
            .ToListAsync(cancellationToken);

        return new GetPropertyByIdResponse
        {
            Id = property.Id,
            HostId = property.HostId,
            PropertyType = property.PropertyType,
            Name = new Dictionary<string, string>(property.Name.Values),
            City = property.City,
            Units =
            [
                .. units.Select(u => new UnitSummary
                {
                    Id = u.Id,
                    Name = new Dictionary<string, string>(u.Name.Values),
                    MaxOccupancy = u.MaxOccupancy,
                    BasePrice = u.BasePrice,
                    Currency = u.Currency
                })
            ]
        };
    }
}
