using BuildingBlocks.Pagination;
using Catalog.Entities;
using Catalog.Features.GetProperties;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Features.GetMyProperties;

// Separate request/handler from GetPropertiesHandler on purpose, even
// though the query differs from the public one only by which filter it
// applies - see GetPropertiesRequest's own comment. Sharing the Mediator
// request type here would mean:
//   1. TelemetryPipelineBehavior keys every span/metric/log line off
//      typeof(TMessage).Name alone, so an anonymous browse call and an
//      authenticated host's own-listings call would be indistinguishable
//      in traces/metrics - different caller population, different traffic
//      shape, different alerting needs, collapsed into one bucket.
//   2. The request's HostId field would need two different trust levels
//      depending on which endpoint populated it (derived-from-claim vs.
//      anonymous-client-input), which is exactly the kind of implicit
//      coupling that becomes a real bug once one caller evolves
//      independently of the other.
// The query-building/mapping logic is still shared, just as a plain method
// call (PropertySummaryMapper), not through the Mediator dispatch layer.
public class GetMyPropertiesHandler(
    AppCatalogDbContext dbContext,
    IHostAuthorization hostAuthorization) : IRequestHandler<GetMyPropertiesRequest, PagedResponse<PropertySummary>>
{
    public async ValueTask<PagedResponse<PropertySummary>> Handle(GetMyPropertiesRequest request, CancellationToken cancellationToken)
    {
        Guid hostId = hostAuthorization.RequireHostId();

        // Id as a tiebreaker convention, not a deliberate sort - see
        // GetPropertiesHandler's identical comment.
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
