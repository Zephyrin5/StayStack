using BuildingBlocks.Pagination;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Promotions.Entities;
namespace Promotions.Features.ListMyPromotions;

public class ListMyPromotionsHandler(
    AppPromotionsDbContext dbContext,
    IHostAuthorization hostAuthorization) : IRequestHandler<ListMyPromotionsRequest, PagedResponse<PromotionSummary>>
{
    public async ValueTask<PagedResponse<PromotionSummary>> Handle(
        ListMyPromotionsRequest request, CancellationToken cancellationToken)
    {
        Guid hostId = hostAuthorization.RequireHostId();

        // Scoped to codes this host created - not platform-wide codes too,
        // since those aren't this host's to manage (edit/archive) even
        // though they'd also apply to this host's units. Id as a
        // tiebreaker, not a deliberate sort - see docs/adr/0008.
        (List<Promotion> promotions, int totalCount) = await dbContext.Promotions
            .AsNoTracking()
            .Where(p => p.HostId == hostId)
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
