using BuildingBlocks.Exceptions;
using Catalog.Contracts;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Promotions.Entities;
using Promotions.Enums;
using SeedWork.ValueObjects;
using System.Data;
namespace Promotions.Contracts;

// internal, same reasoning as Catalog.Contracts.HoldConfirmation - Bookings
// should only ever reach this through IPromotionRedemption, resolved via DI.
internal class PromotionRedemption(
    AppPromotionsDbContext dbContext,
    IUnitLookup unitLookup,
    TimeProvider timeProvider) : IPromotionRedemption
{
    public async Task<PromotionRedemptionResult> RedeemAsync(
        string code,
        Guid unitId,
        string guestEmail,
        Money subtotal,
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
            // Resolved through IUnitLookup rather than reading Unit/Property
            // directly - this module has no reference to Catalog or
            // AppCatalogDbContext at all (docs/adr/0004). UnitSummary
            // already carries HostId (added for Reviews' own cross-module
            // need), so this is one call instead of two local EF reads.
            UnitSummary unit = await unitLookup.GetUnitAsync(unitId, cancellationToken)
                                ?? throw new NotFoundException("Unit", unitId);

            if (unit.HostId != promotion.HostId)
            {
                throw new PromotionInvalidException($"Promo code '{code}' is not valid for this property.");
            }
        }

        if (promotion.DiscountType == PromotionDiscountType.FixedAmount && promotion.Currency != subtotal.Currency)
        {
            throw new PromotionInvalidException($"Promo code '{code}' is not valid in this currency.");
        }

        Money discountAmount = ComputeDiscountAmount(promotion, subtotal);

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
                        DiscountAmount = discountAmount.Amount,
                        Currency = discountAmount.Currency.ToString(),
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

            // UPDATE, not DELETE - the row survives as history (see
            // PromotionRedemption.ReversedAt's own doc comment), and the
            // "already reversed" guard (reversed_at IS NULL) makes this
            // idempotent the same way a plain DELETE naturally was: calling
            // it twice for the same booking only ever affects the row once.
            const string reverseSql = """
                                      UPDATE promotion_redemptions
                                      SET reversed_at = @Now
                                      WHERE booking_id = @BookingId AND reversed_at IS NULL
                                      RETURNING promotion_id AS "PromotionId";
                                      """;

            Guid? promotionId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                reverseSql, new { BookingId = bookingId, Now = timeProvider.GetUtcNow() }, transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            if (promotionId is null)
            {
                // No-op - this booking never redeemed a code, or its
                // redemption was already reversed - same idempotent shape
                // as Catalog.Contracts.IHoldConfirmation.ReleaseHoldAsync.
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
    internal static Money ComputeDiscountAmount(Promotion promotion, Money subtotal)
    {
        // promotion.DiscountValue is already validated to match
        // subtotal.Currency for FixedAmount (see the check in RedeemAsync);
        // Percentage is currency-agnostic by construction (Promotion.Currency
        // is null for it), so the result just inherits subtotal's currency
        // either way.
        decimal discount = promotion.DiscountType == PromotionDiscountType.Percentage
            ? subtotal.Amount * promotion.DiscountValue / 100m
            : promotion.DiscountValue;

        return Money.Of(Math.Min(discount, subtotal.Amount), subtotal.Currency);
    }
}
