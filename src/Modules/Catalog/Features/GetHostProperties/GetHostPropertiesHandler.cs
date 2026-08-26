using BuildingBlocks.Exceptions;
using BuildingBlocks.Pagination;
using Catalog.Entities;
using Catalog.Features.GetProperties;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Features.GetHostProperties;

public class GetHostPropertiesHandler(
    AppCatalogDbContext dbContext,
    IHostLookup hostLookup) : IRequestHandler<GetHostPropertiesRequest, PagedResponse<PropertySummary>>
{
    public async ValueTask<PagedResponse<PropertySummary>> Handle(GetHostPropertiesRequest request, CancellationToken cancellationToken)
    {
        // Trusted-but-verified, same reasoning as AdminCreatePropertyHandler:
        // an Administrator is allowed to name any HostId, but that doesn't
        // mean the one on this request is real.
        if (!await hostLookup.ExistsAsync(request.HostId, cancellationToken))
        {
            throw new NotFoundException("Host", request.HostId);
        }

        // Id as a tiebreaker, not deliberate sort criteria - see docs/adr/0008.
        (List<Property> properties, int totalCount) = await dbContext.Properties
            .AsNoTracking()
            .Where(p => p.HostId == request.HostId)
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
