using BuildingBlocks.Pagination;
using Catalog.Entities;
using Catalog.Features.GetProperties;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Features.GetMyProperties;

// Separate request/handler from GetPropertiesHandler on purpose - see
// docs/adr/0007. Query-building/mapping logic is still shared, just as a
// plain method call (PropertySummaryMapper), not through Mediator.
public class GetMyPropertiesHandler(
    AppCatalogDbContext dbContext,
    IHostAuthorization hostAuthorization) : IRequestHandler<GetMyPropertiesRequest, PagedResponse<PropertySummary>>
{
    public async ValueTask<PagedResponse<PropertySummary>> Handle(GetMyPropertiesRequest request, CancellationToken cancellationToken)
    {
        Guid hostId = hostAuthorization.RequireHostId();

        // Id as a tiebreaker, not a deliberate sort - see docs/adr/0008.
        (List<Property> properties, int totalCount) = await dbContext.Properties
            .AsNoTracking()
            .Where(p => p.HostId == hostId)
            .OrderBy(p => p.Id)
            .ToPagedListAsync(request.Page, request.PageSize, cancellationToken);

        return new PagedResponse<PropertySummary>
        {
            Items = PropertySummaryMapper.Map(properties),
            Page = request.Page,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }
}
