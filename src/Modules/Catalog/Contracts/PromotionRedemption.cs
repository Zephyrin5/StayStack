using BuildingBlocks.Exceptions;
using Catalog.Entities;
using Catalog.Enums;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SeedWork.Enums;
using System.Data;
using NotFoundException = BuildingBlocks.Exceptions.NotFoundException;
using Unit = Catalog.Entities.Unit;

namespace Catalog.Contracts;

// internal, same reasoning as HoldConfirmation - Bookings should only ever
// reach this through IPromotionRedemption, resolved via DI.
internal class PromotionRedemption(AppCatalogDbContext dbContext, TimeProvider timeProvider) : IPromotionRedemption
{
    public async Task<PromotionRedemptionResult> RedeemAsync(
        string code,
        Guid unitId,
        string guestEmail,
        decimal subtotal,
        Currency currency,
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        string normalizedCode = code.Trim().ToUpperInvariant();
        string normalizedEmail = guestEmail.Trim().ToLowerInvariant();

        Promotion promotion = await dbContext.Promotions
                                   .SingleOrDefaultAsync(p => p.Code == normalizedCode, cancellationToken)
                               ?? throw new PromotionInvalidException($"Promo code '{code}' does not exist.");

        if (promotion.ExpiresAt is not null && promotion.ExpiresAt <= timeProvider.GetUtcNow())
        {
            throw new PromotionInvalidException($"Promo code '{code}' has expired.");
        }

        if (promotion.HostId is not null)
        {
            Unit unit = await dbContext.Units.SingleOrDefaultAsync(u => u.Id == unitId, cancellationToken)
                        ?? throw new NotFoundException(nameof(Unit), unitId);
            Property property = await dbContext.Properties
                                     .SingleOrDefaultAsync(p => p.Id == unit.PropertyId, cancellationToken)
                                 ?? throw new NotFoundException(nameof(Property), unit.PropertyId);

            if (property.HostId != promotion.HostId)
            {
                throw new PromotionInvalidException($"Promo code '{code}' is not valid for this property.");
            }
        }

        if (promotion.DiscountType == PromotionDiscountType.FixedAmount && promotion.Currency != currency)
        {
            throw new PromotionInvalidException($"Promo code '{code}' is not valid in this currency.");
        }

        decimal discountAmount = ComputeDiscountAmount(promotion, subtotal);

        // Wrapped in the execution strategy, not called bare - same
        // deadlock/retry reasoning as HoldAvailabilityHandler. Two
        // statements share one transaction here (the redemption-cap
        // increment and the PromotionRedemption insert) so a unique-email
        // violation on the insert rolls back the increment too - a
        // rejected duplicate attempt never burns a redemption slot.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        Guid redemptionId = await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            IDbConnection connection = dbContext.Database.GetDbConnection();

            const string capSql = """
                                  UPDATE promotions
                                  SET redemption_count = redemption_count + 1
                                  WHERE id = @PromotionId AND (max_redemptions IS NULL OR redemption_count < max_redemptions);
                                  """;

            int rowsAffected = await connection.ExecuteAsync(new CommandDefinition(
                capSql, new { PromotionId = promotion.Id }, transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            if (rowsAffected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new PromotionInvalidException($"Promo code '{code}' has reached its redemption limit.");
            }

            Guid newRedemptionId = Guid.CreateVersion7();

            const string insertSql = """
                                     INSERT INTO promotion_redemptions (id, promotion_id, booking_id, guest_email, discount_amount, currency, redeemed_at)
                                     VALUES (@Id, @PromotionId, @BookingId, @GuestEmail, @DiscountAmount, @Currency, @RedeemedAt);
                                     """;

            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    insertSql,
                    new
                    {
                        Id = newRedemptionId,
                        PromotionId = promotion.Id,
                        BookingId = bookingId,
                        GuestEmail = normalizedEmail,
                        DiscountAmount = discountAmount,
                        Currency = currency.ToString(),
                        RedeemedAt = timeProvider.GetUtcNow()
                    },
                    transaction.GetDbTransaction(),
                    cancellationToken: cancellationToken));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // The one-per-guest-email index rejected the insert - this
                // guest already redeemed this code. Not classified as
                // transient, so it propagates straight out of ExecuteAsync
                // instead of being retried.
                await transaction.RollbackAsync(cancellationToken);
                throw new PromotionInvalidException($"Promo code '{code}' has already been used by this email address.");
            }

            await transaction.CommitAsync(cancellationToken);
            return newRedemptionId;
        });

        return new PromotionRedemptionResult { RedemptionId = redemptionId, DiscountAmount = discountAmount };
    }

    public async Task ReverseRedemptionAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            IDbConnection connection = dbContext.Database.GetDbConnection();

            const string deleteSql = """
                                     DELETE FROM promotion_redemptions
                                     WHERE booking_id = @BookingId
                                     RETURNING promotion_id AS "PromotionId";
                                     """;

            Guid? promotionId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                deleteSql, new { BookingId = bookingId }, transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            if (promotionId is null)
            {
                // No-op - this booking never redeemed a code, same
                // idempotent shape as IHoldConfirmation.ReleaseHoldAsync.
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            const string decrementSql = """
                                        UPDATE promotions
                                        SET redemption_count = redemption_count - 1
                                        WHERE id = @PromotionId;
                                        """;

            await connection.ExecuteAsync(new CommandDefinition(
                decrementSql, new { PromotionId = promotionId }, transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
        });
    }

    // internal, not private - exercised directly by PromotionRedemptionTests
    // (UnitTests) via InternalsVisibleTo, rather than only indirectly
    // through a full RedeemAsync call.
    internal static decimal ComputeDiscountAmount(Promotion promotion, decimal subtotal)
    {
        decimal discount = promotion.DiscountType == PromotionDiscountType.Percentage
            ? subtotal * promotion.DiscountValue / 100m
            : promotion.DiscountValue;

        return Math.Min(discount, subtotal);
    }
}
