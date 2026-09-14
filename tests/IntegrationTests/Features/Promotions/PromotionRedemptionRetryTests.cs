// AUDIT 2026-09-14: Injects post-commit (TransactionCommittedAsync), targeted by a durable redemption for the booking; fresh-scope asserts. Reproduced the exact rejection before bef3fb7 fixed it.
using BuildingBlocks.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Persistence.Interceptors;
using Promotions;
using Promotions.Contracts;
using Promotions.Entities;
using Promotions.Enums;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Data.Common;
namespace IntegrationTests.Features.Promotions;

// The sixth instance of docs/adr/0025's identity rule, and the first one found by
// RetryIdentityProtocolTests rather than by hand.
//
// RedeemAsync minted the redemption id inside its retried delegate. After a
// commit lost its acknowledgement, the retry held a new id, re-ran the cap
// increment, and its insert met the first attempt's committed row on the
// one-redemption-per-email index - so the guest's own redemption came back as
// "already used by this email address", and the checkout it belonged to failed.
[Collection("Integration Tests")]
public class PromotionRedemptionRetryTests(IntegrationTestWebApplicationFactory factory)
{
    // After the commit, and only a commit that made a redemption for the target
    // booking durable. The insert is Dapper, so there is nothing in the change
    // tracker to recognise it by; a separate connection can see the committed
    // row. Targeted because this host runs TickerQ, whose jobs commit on their
    // own schedule.
    private sealed class LoseTheAckOnFirstRedemptionCommitFor(ICurrentUserProvider currentUser, TimeProvider timeProvider)
        : AuditableEntitySaveChangesInterceptor(currentUser, timeProvider), IDbTransactionInterceptor
    {
        private static Guid _bookingId;
        private static int _fired;

        public static void ArmFor(Guid bookingId)
        {
            Interlocked.Exchange(ref _fired, 0);
            _bookingId = bookingId;
        }

        public static void Disarm() => _bookingId = Guid.Empty;

        public static bool Fired => Volatile.Read(ref _fired) > 0;

        public async Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Guid bookingId = _bookingId;

            if (bookingId == Guid.Empty
                || eventData.Context is not AppPromotionsDbContext context
                || Volatile.Read(ref _fired) != 0)
            {
                return;
            }

            await using NpgsqlConnection probe = new NpgsqlConnection(context.Database.GetConnectionString());
            await probe.OpenAsync(cancellationToken);

            await using NpgsqlCommand command = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM promotion_redemptions WHERE booking_id = @BookingId)", probe);
            command.Parameters.AddWithValue("BookingId", bookingId);

            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!
                && Interlocked.Increment(ref _fired) == 1)
            {
                throw new PostgresException(
                    "simulated lost acknowledgement after commit", "ERROR", "ERROR", "40001");
            }
        }
    }

    [Fact]
    public async Task ARedemptionWhoseCommitLosesItsAcknowledgement_ReturnsTheRedemptionItAlreadyMade()
    {
        Promotion promotion = Promotion.CreatePlatformPromotion(
            $"RETRY{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            PromotionDiscountType.Percentage,
            10m,
            null,
            expiresAt: null,
            maxRedemptions: null,
            hostId: null);

        using (IServiceScope seed = factory.Services.CreateScope())
        {
            AppPromotionsDbContext context = seed.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
            context.Add(promotion);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddScoped<AuditableEntitySaveChangesInterceptor, LoseTheAckOnFirstRedemptionCommitFor>()));

        Guid bookingId = Guid.CreateVersion7();
        PromotionRedemptionResult result;

        using (IServiceScope scope = host.Services.CreateScope())
        {
            LoseTheAckOnFirstRedemptionCommitFor.ArmFor(bookingId);

            try
            {
                result = await scope.ServiceProvider.GetRequiredService<IPromotionRedemption>().RedeemAsync(
                    promotion.Code, Guid.NewGuid(), "guest@example.com",
                    Money.Of(200m, Currency.KWD), bookingId, TestContext.Current.CancellationToken);
            }
            finally
            {
                LoseTheAckOnFirstRedemptionCommitFor.Disarm();
            }
        }

        Assert.True(LoseTheAckOnFirstRedemptionCommitFor.Fired, "The lost acknowledgement never reached the redemption.");

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppPromotionsDbContext db = assertScope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();

        // One redemption, the one returned, and one slot counted - a retry that
        // re-ran the cap increment and kept it would burn a second.
        List<global::Promotions.Entities.PromotionRedemption> redemptions = await db.PromotionRedemptions.AsNoTracking()
            .Where(r => r.BookingId == bookingId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(result.RedemptionId, Assert.Single(redemptions).Id);

        Promotion persisted = await db.Promotions.AsNoTracking()
            .SingleAsync(p => p.Id == promotion.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, persisted.RedemptionCount);
    }
}
