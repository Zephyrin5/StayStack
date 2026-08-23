using BuildingBlocks.Pagination;
using Catalog.Entities;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Features.GetProperties;

public class GetPropertiesHandler(AppCatalogDbContext dbContext) : IRequestHandler<GetPropertiesRequest, PagedResponse<PropertySummary>>
{
    public async ValueTask<PagedResponse<PropertySummary>> Handle(GetPropertiesRequest request, CancellationToken cancellationToken)
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

        // Id as the sort key is really a tiebreaker convention, not a
        // deliberate "browse by creation order" choice - it's what keeps
        // Skip/Take pagination stable across requests (a Guid.CreateVersion7
        // id only ever appends, never shifts under a filter or a later
        // insert). If a real sort ever gets added here (price, rating,
        // relevance), it needs to be `.OrderBy(p => p.SomeField).ThenBy(p =>
        // p.Id)`, not a bare `.OrderBy(p => p.SomeField)` - without the
        // tiebreaker, ties in that field make page boundaries
        // non-deterministic (duplicate/skipped rows across pages). A sort
        // key that can change value after the fact (unlike Id) is a
        // different, harder problem Skip/Take can't fully solve - that's
        // keyset/cursor pagination, deliberately not built here since
        // nothing needs it yet.
        (List<Property> properties, int totalCount) = await query
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
