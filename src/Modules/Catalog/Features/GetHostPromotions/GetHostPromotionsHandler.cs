using BuildingBlocks.Exceptions;
using BuildingBlocks.Pagination;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Features.GetHostPromotions;

public class GetHostPromotionsHandler(
    AppCatalogDbContext dbContext,
    IHostLookup hostLookup) : IRequestHandler<GetHostPromotionsRequest, PagedResponse<PromotionSummary>>
{
    public async ValueTask<PagedResponse<PromotionSummary>> Handle(
        GetHostPromotionsRequest request, CancellationToken cancellationToken)
    {
        // Trusted-but-verified, same reasoning as AdminCreatePromotionHandler:
        // an Administrator is allowed to name any HostId, but that doesn't
        // mean the one on this request is real.
        if (!await hostLookup.ExistsAsync(request.HostId, cancellationToken))
        {
            throw new NotFoundException("Host", request.HostId);
        }

        (List<Promotion> promotions, int totalCount) = await dbContext.Promotions
            .AsNoTracking()
            .Where(p => p.HostId == request.HostId)
            .OrderBy(p => p.Id)
            .ToPagedListAsync(request.Page, request.PageSize, cancellationToken);

        return new PagedResponse<PromotionSummary>
        {
            Items = PromotionSummaryMapper.Map(promotions),
            Page = request.Page,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }
}
