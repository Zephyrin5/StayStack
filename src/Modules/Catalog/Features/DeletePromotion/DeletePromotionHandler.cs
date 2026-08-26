using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Features.DeletePromotion;

public class DeletePromotionHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
    TimeProvider timeProvider) : IRequestHandler<DeletePromotionRequest, DeletePromotionResponse>
{
    public async ValueTask<DeletePromotionResponse> Handle(
        DeletePromotionRequest request, CancellationToken cancellationToken)
    {
        Promotion? promotion = await dbContext.Promotions
            .SingleOrDefaultAsync(p => p.Id == request.PromotionId, cancellationToken);

        if (promotion is null)
        {
            throw new NotFoundException(nameof(Promotion), request.PromotionId);
        }

        if (!currentUserProvider.Roles.Contains("Administrator"))
        {
            if (promotion.HostId is null)
            {
                throw new NotFoundException(nameof(Promotion), promotion.Id);
            }

            hostAuthorization.RequireOwnership(promotion.HostId.Value, nameof(Promotion), promotion.Id);
        }

        // Soft delete - existing PromotionRedemption rows and their audit
        // trail stay intact. The global soft-delete query filter (see
        // StayStackDbContext) means an archived code also naturally stops
        // resolving via PromotionRedemption.RedeemAsync's own Code lookup,
        // with no extra Status check needed there.
        promotion.Archive(timeProvider.GetUtcNow(), currentUserProvider.UserId);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new DeletePromotionResponse { PromotionId = promotion.Id };
    }
}
