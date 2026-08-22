using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Features.GetProperties;

public class GetPropertiesHandler(AppCatalogDbContext dbContext) : IRequestHandler<GetPropertiesRequest, GetPropertiesResponse>
{
    public async ValueTask<GetPropertiesResponse> Handle(GetPropertiesRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.Properties.AsNoTracking();

        if (request.City is not null)
        {
            query = query.Where(p => p.City == request.City);
        }

        if (request.PropertyType is not null)
        {
            query = query.Where(p => p.PropertyType == request.PropertyType);
        }

        // Materialize first, project after - Name is a LocalizedText (a
        // value-converted jsonb column via StayStackDbContext's global
        // convention), and EF Core can't translate .Values access on a
        // converted CLR type into SQL inside a server-side .Select().
        var properties = await query.ToListAsync(cancellationToken);

        return new GetPropertiesResponse
        {
            Properties =
            [
                .. properties.Select(p => new PropertySummary
                {
                    Id = p.Id,
                    HostId = p.HostId,
                    PropertyType = p.PropertyType,
                    Name = new Dictionary<string, string>(p.Name.Values),
                    City = p.City
                })
            ]
        };
    }
}
