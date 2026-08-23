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

        var properties = await query.ToListAsync(cancellationToken);

        return new GetPropertiesResponse { Properties = PropertySummaryMapper.Map(properties) };
    }
}
