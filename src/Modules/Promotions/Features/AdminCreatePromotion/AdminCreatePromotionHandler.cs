using BuildingBlocks.Exceptions;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Promotions.Entities;
using Promotions.Features.CreatePromotion;
namespace Promotions.Features.AdminCreatePromotion;

public class AdminCreatePromotionHandler(
    AppPromotionsDbContext dbContext,
    IHostLookup hostLookup) : IRequestHandler<AdminCreatePromotionRequest, CreatePromotionResponse>
{
    public async ValueTask<CreatePromotionResponse> Handle(
        AdminCreatePromotionRequest request, CancellationToken cancellationToken)
    {
        // Unlike CreatePromotionHandler, HostId here IS trusted client
        // input - but "trusted" (an Administrator is allowed to specify
        // it) doesn't mean "assumed valid". Only checked when set - null
        // is a deliberate platform-wide code, not an omission to catch.
        if (request.HostId is not null && !await hostLookup.ExistsAsync(request.HostId.Value, cancellationToken))
        {
            throw new NotFoundException("Host", request.HostId.Value);
        }

        Promotion promotion;
        try
        {
            promotion = Promotion.CreatePlatformPromotion(
                request.Code, request.DiscountType, request.DiscountValue, request.Currency,
                request.ExpiresAt, request.MaxRedemptions, request.HostId);
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
