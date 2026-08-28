using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Promotions.Entities;
namespace Promotions.Features.UpdatePromotion;

public class UpdatePromotionHandler(
    AppPromotionsDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization) : IRequestHandler<UpdatePromotionRequest, UpdatePromotionResponse>
{
    public async ValueTask<UpdatePromotionResponse> Handle(
        UpdatePromotionRequest request, CancellationToken cancellationToken)
    {
        Promotion? promotion = await dbContext.Promotions
            .SingleOrDefaultAsync(p => p.Id == request.PromotionId, cancellationToken);

        if (promotion is null)
        {
            throw new NotFoundException(nameof(Promotion), request.PromotionId);
        }

        if (!currentUserProvider.Roles.Contains(AuthorizationPolicies.Administrator))
        {
            // A platform-wide promotion (HostId null) has no owning host to
            // check against - only an Administrator can touch it. Same
            // "doesn't exist and exists-but-isn't-yours must look
            // identical" reasoning as RequireOwnership itself.
            if (promotion.HostId is null)
            {
                throw new NotFoundException(nameof(Promotion), promotion.Id);
            }

            hostAuthorization.RequireOwnership(promotion.HostId.Value, nameof(Promotion), promotion.Id);
        }

        try
        {
            promotion.SetDiscountValue(request.DiscountValue);
            promotion.SetCurrency(request.Currency);
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(nameof(request.Currency), ex.Message);
        }

        promotion.SetExpiresAt(request.ExpiresAt);
        promotion.SetMaxRedemptions(request.MaxRedemptions);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new UpdatePromotionResponse { PromotionId = promotion.Id };
    }
}
