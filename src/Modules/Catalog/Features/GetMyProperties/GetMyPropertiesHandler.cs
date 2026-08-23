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
    IHostAuthorization hostAuthorization) : IRequestHandler<GetMyPropertiesRequest, GetPropertiesResponse>
{
    public async ValueTask<GetPropertiesResponse> Handle(GetMyPropertiesRequest request, CancellationToken cancellationToken)
    {
        Guid hostId = hostAuthorization.RequireHostId();

        var properties = await dbContext.Properties
            .AsNoTracking()
            .Where(p => p.HostId == hostId)
            .ToListAsync(cancellationToken);

        return new GetPropertiesResponse { Properties = PropertySummaryMapper.Map(properties) };
    }
}
