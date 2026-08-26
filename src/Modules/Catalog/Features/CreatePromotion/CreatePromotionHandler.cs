using BuildingBlocks.Exceptions;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace Catalog.Features.CreatePromotion;

public class CreatePromotionHandler(
    AppCatalogDbContext dbContext,
    IHostAuthorization hostAuthorization) : IRequestHandler<CreatePromotionRequest, CreatePromotionResponse>
{
    public async ValueTask<CreatePromotionResponse> Handle(
        CreatePromotionRequest request, CancellationToken cancellationToken)
    {
        Guid hostId = hostAuthorization.RequireHostId();

        Promotion promotion;
        try
        {
            promotion = Promotion.CreateHostPromotion(
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ValidationException(nameof(request.Code), $"Promo code '{request.Code}' is already in use.");
        }

        return new CreatePromotionResponse { PromotionId = promotion.Id };
    }
}
