using Promotions.Entities.Configurations;
using Persistence;
using BuildingBlocks.Exceptions;
using Catalog.Contracts;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Promotions.Entities;
using Promotions.Enums;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Data;
using System.Data.Common;
namespace Promotions.Contracts;

// internal, same reasoning as Catalog.Contracts.HoldConfirmation - Bookings
// should only ever reach this through IPromotionRedemption, resolved via DI.
internal class PromotionRedemption(
    PromotionsDb dbContext,
    IUnitLookup unitLookup,
    TimeProvider timeProvider) : IPromotionRedemption
{
    public async Task<PromotionRedemptionResult> RedeemAsync(
        string code,
        Guid unitId,
        string guestEmail,
        Money subtotal,
        Guid bookingId,
        Guid redemptionId,
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
            // CatalogDb at all (docs/adr/0004). UnitSummary
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

        // The caller's atomic scope owns the transaction: the cap increment and the
        // insert below commit with the booking they discount, or not at all
        // (ConfirmBookingHandler). A rejected duplicate therefore never burns a
        // redemption slot, and nothing here commits, rolls back or retries.
        DbTransaction transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
                                    ?? throw new InvalidOperationException(
                                        $"{nameof(PromotionRedemption)}.{nameof(RedeemAsync)} must run inside the caller's " +
                                        "atomic scope: a redemption committed on its own survives the checkout it discounts failing.");
        IDbConnection connection = dbContext.Database.GetDbConnection();

        // Every condition that decides whether this redemption is LEGAL
        // lives in this one predicate, evaluated against the row this
        // statement locks - not against the snapshot read above.
        //
        // The cap was always here, because a count obviously races. Expiry
        // and archival race in exactly the same shape for two different
        // reasons: expires_at is mutable (Promotion.SetExpiresAt), and it is
        // also compared against a clock that keeps moving, so a code can lapse
        // between the read and this write with nobody editing anything.
        // Archival is the sharper of the two - the snapshot read goes through
        // the soft-delete query filter, so a promotion deleted a moment later
        // would still be redeemable without this predicate.
        //
        // The soft-delete predicate restated, the same one PromotionConfiguration's partial unique
        // index uses.
        //
        // Host ownership is deliberately NOT here, and that is not an
        // omission: Promotion.HostId is set in the constructor and has no
        // mutator, so the value the snapshot read saw is the value this
        // row will always have. There is nothing to race.
        string capSql = $"""
                              UPDATE {PromotionsModel.Schema}.promotions
                              SET redemption_count = redemption_count + 1
                              WHERE id = @PromotionId
                                AND {SoftDelete.NotArchived}
                                AND (expires_at IS NULL OR expires_at > @Now)
                                AND (max_redemptions IS NULL OR redemption_count < max_redemptions);
                              """;

        int rowsAffected = await connection.ExecuteAsync(new CommandDefinition(
            capSql, new { PromotionId = promotion.Id, Now = timeProvider.GetUtcNow() },
            transaction, cancellationToken: cancellationToken));

        if (rowsAffected == 0)
        {
            // One predicate covers three reasons, so the row is re-read to say
            // which - a merged "this code isn't valid" would be a worse answer to
            // a guest. Only ever runs on the failure path, and inside the same
            // transaction, so it sees the same row the UPDATE just declined to
            // touch.
            PromotionStateRow? state = await connection.QuerySingleOrDefaultAsync<PromotionStateRow>(
                new CommandDefinition(
                    $"""
                    SELECT status AS "Status", expires_at AS "ExpiresAt",
                           redemption_count AS "RedemptionCount", max_redemptions AS "MaxRedemptions"
                    FROM {PromotionsModel.Schema}.promotions WHERE id = @PromotionId;
                    """,
                    new { PromotionId = promotion.Id }, transaction,
                    cancellationToken: cancellationToken));

            throw new PromotionInvalidException(DescribeRejection(code, state));
        }

        const string insertSql = $"""
                                 INSERT INTO {PromotionsModel.Schema}.promotion_redemptions (id, promotion_id, booking_id, guest_email, discount_amount, currency, redeemed_at)
                                 VALUES (@Id, @PromotionId, @BookingId, @GuestEmail, @DiscountAmount, @Currency, @RedeemedAt);
                                 """;

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                insertSql,
                new
                {
                    Id = redemptionId,
                    PromotionId = promotion.Id,
                    BookingId = bookingId,
                    GuestEmail = normalizedEmail,
                    DiscountAmount = discountAmount.Amount,
                    // The enum, not .ToString() - CurrencyTypeHandler
                    // writes the character(3) code.
                    discountAmount.Currency,
                    RedeemedAt = timeProvider.GetUtcNow()
                },
                transaction,
                cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.IsViolationOf(PromotionRedemptionConfiguration.PromotionEmailIndex))
        {
            // The one-per-guest-email index rejected the insert - this guest
            // already redeemed this code. The failed statement aborted the
            // transaction, so the caller's scope rolls back, cap increment
            // included.
            throw new PromotionInvalidException($"Promo code '{code}' has already been used by this email address.");
        }

        return new PromotionRedemptionResult { RedemptionId = redemptionId, DiscountAmount = discountAmount };
    }

    public async Task ReverseRedemptionAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // The caller's atomic scope owns the transaction, so the code goes back
        // only if the cancellation that frees it commits.
        DbTransaction transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
                                    ?? throw new InvalidOperationException(
                                        $"{nameof(PromotionRedemption)}.{nameof(ReverseRedemptionAsync)} must run inside the caller's " +
                                        "atomic scope: a reversal committed on its own frees a code for a booking whose cancellation may roll back.");
        IDbConnection connection = dbContext.Database.GetDbConnection();

        // UPDATE, not DELETE - the row survives as history (see
        // PromotionRedemption.ReversedAt's own doc comment), and the
        // "already reversed" guard (reversed_at IS NULL) makes this
        // idempotent: calling it twice for the same booking only ever affects
        // the row once.
        const string reverseSql = $"""
                                  UPDATE {PromotionsModel.Schema}.promotion_redemptions
                                  SET reversed_at = @Now
                                  WHERE booking_id = @BookingId AND reversed_at IS NULL
                                  RETURNING promotion_id AS "PromotionId";
                                  """;

        Guid? promotionId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            reverseSql, new { BookingId = bookingId, Now = timeProvider.GetUtcNow() }, transaction,
            cancellationToken: cancellationToken));

        if (promotionId is null)
        {
            // No-op - this booking never redeemed a code, or its
            // redemption was already reversed - same idempotent shape
            // as Catalog.Contracts.IHoldConfirmation.ReleaseHoldAsync.
            return;
        }

        const string decrementSql = $"""
                                    UPDATE {PromotionsModel.Schema}.promotions
                                    SET redemption_count = redemption_count - 1
                                    WHERE id = @PromotionId;
                                    """;

        await connection.ExecuteAsync(new CommandDefinition(
            decrementSql, new { PromotionId = promotionId }, transaction,
            cancellationToken: cancellationToken));
    }

    // Shape of the failure-path diagnostic read above. Nullable Status so a
    // row that vanished entirely still materializes rather than throwing
    // inside the rejection path.
    private sealed record PromotionStateRow
    {
        public int Status { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public int RedemptionCount { get; init; }
        public int? MaxRedemptions { get; init; }
    }

    // Ordered to match the predicate's own reading order, so the message
    // names the first reason the row was rejected rather than an arbitrary
    // one when several apply at once.
    private string DescribeRejection(string code, PromotionStateRow? state)
    {
        if (state is null)
        {
            return $"Promo code '{code}' does not exist.";
        }

        if (state.Status == SoftDelete.ArchivedStatus)
        {
            return $"Promo code '{code}' does not exist.";
        }

        if (state.ExpiresAt is not null && state.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return $"Promo code '{code}' has expired.";
        }

        return $"Promo code '{code}' has reached its redemption limit.";
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
