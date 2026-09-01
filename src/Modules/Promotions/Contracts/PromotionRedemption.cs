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

            // Every condition that decides whether this redemption is LEGAL
            // lives in this one predicate, evaluated against the row this
            // statement locks - not against the snapshot read above.
            //
            // The cap was always here, because a count obviously races. Expiry
            // and archival were not, and they race in exactly the same shape
            // for two different reasons: expires_at is mutable
            // (Promotion.SetExpiresAt), and it is also compared against a
            // clock that keeps moving, so a code can lapse between the read
            // and this write with nobody editing anything. Archival is the
            // sharper of the two - the snapshot read goes through the
            // soft-delete query filter, so a promotion deleted a moment later
            // was still redeemable here.
            //
            // status <> 2 is EntityStatus.Archived; Postgres only ever sees
            // the stored int, same predicate PromotionConfiguration's partial
            // unique index already uses.
            //
            // Host ownership is deliberately NOT here, and that is not an
            // omission: Promotion.HostId is set in the constructor and has no
            // mutator, so the value the snapshot read saw is the value this
            // row will always have. There is nothing to race.
            const string capSql = """
                                  UPDATE promotions
                                  SET redemption_count = redemption_count + 1
                                  WHERE id = @PromotionId
                                    AND status <> 2
                                    AND (expires_at IS NULL OR expires_at > @Now)
                                    AND (max_redemptions IS NULL OR redemption_count < max_redemptions);
                                  """;

            int rowsAffected = await connection.ExecuteAsync(new CommandDefinition(
                capSql, new { PromotionId = promotion.Id, Now = timeProvider.GetUtcNow() },
                transaction.GetDbTransaction(), cancellationToken: cancellationToken));

            if (rowsAffected == 0)
            {
                // One predicate now covers three reasons, so the row is
                // re-read to say which - a merged "this code isn't valid"
                // would be a worse answer to a guest than the specific one
                // they got before. Only ever runs on the failure path, and
                // inside the same transaction, so it sees the same row the
                // UPDATE just declined to touch.
                PromotionStateRow? state = await connection.QuerySingleOrDefaultAsync<PromotionStateRow>(
                    new CommandDefinition(
                        """
                        SELECT status AS "Status", expires_at AS "ExpiresAt",
                               redemption_count AS "RedemptionCount", max_redemptions AS "MaxRedemptions"
                        FROM promotions WHERE id = @PromotionId;
                        """,
                        new { PromotionId = promotion.Id }, transaction.GetDbTransaction(),
                        cancellationToken: cancellationToken));

                await transaction.RollbackAsync(cancellationToken);

                throw new PromotionInvalidException(DescribeRejection(code, state));
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

        if (state.Status == (int)EntityStatus.Archived)
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
