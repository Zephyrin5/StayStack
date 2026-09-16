using Promotions.Entities.Configurations;
using Persistence;
using BuildingBlocks.Exceptions;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Promotions.Entities;
namespace Promotions.Features.CreatePromotion;

public class CreatePromotionHandler(
    PromotionsDb dbContext,
    IHostAuthorization hostAuthorization) : IRequestHandler<CreatePromotionRequest, CreatePromotionResponse>
{
    public async ValueTask<CreatePromotionResponse> Handle(
        CreatePromotionRequest request, CancellationToken cancellationToken)
    {
        // Chosen first, before anything that could retry - see docs/adr/0025.
        Guid promotionId = Guid.CreateVersion7();

        Guid hostId = hostAuthorization.RequireHostId();

        Promotion promotion;
        try
        {
            promotion = Promotion.CreateHostPromotion(
                promotionId,
                hostId, request.Code, request.DiscountType, request.DiscountValue, request.Currency,
                request.ExpiresAt, request.MaxRedemptions);
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(nameof(request.Currency), ex.Message);
        }

        dbContext.Promotions.Add(promotion);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsPrimaryKeyViolationOf(dbContext.Promotions))
        {
            // An earlier attempt committed and lost its acknowledgement - answer
            // with that row (Persistence.CommittedInsertRecovery).
            Promotion committed = await dbContext.Promotions.FindOwnCommittedInsertAsync(promotion.Id, cancellationToken);
            return new CreatePromotionResponse { PromotionId = committed.Id };
        }
        catch (DbUpdateException ex) when (ex.IsViolationOf(PromotionConfiguration.CodeIndex))
        {
            throw new ValidationException(nameof(request.Code), $"Promo code '{request.Code}' is already in use.");
        }

        return new CreatePromotionResponse { PromotionId = promotion.Id };
    }
}
