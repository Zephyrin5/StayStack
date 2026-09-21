// Proves a confirmation whose commit loses its acknowledgement (FailAfterCommit) returns the
// committed booking with a working management token, and that a discounted one counts its
// promotion once. Verified by disabling the committed-booking lookup: both fail. A recovery returning a
// different management token is caught only by the session exchange assertion.
using Bookings;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.CreateBookingSession;
using Bookings.Features.HoldAvailability;
using Catalog;
using Catalog.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Promotions;
using Promotions.Entities;
using Promotions.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Json;
using Persistence;
namespace IntegrationTests.Features.Bookings;

// ConfirmBookingHandler's recovery for a confirmation that committed and lost its
// acknowledgement.
//
// The booking id and the management token's plaintext are chosen before the
// transaction, so a retry finds the committed booking by id and answers with
// it. The assertion that matters is the
// token: a recovery that returns a 200 and a real booking id with a token whose
// hash row rolled back hands out a credential that fails at first use, and a
// test asserting only the status and id passes over that. This one uses the
// token.
[Collection(BookingsCollection.Name)]
public class ConfirmRetryTests(BookingsFixture factory)
{
    private async Task<Guid> HoldAUnitAsync(int daysUntilCheckIn)
    {
        Property property = CatalogSeeding.CreateProperty();
        Unit unit = Unit.Create(
            Guid.CreateVersion7(),
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            100m);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            CatalogDb catalog = scope.ServiceProvider.GetRequiredService<CatalogDb>();
            catalog.AddRange(property, unit);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        DateOnly checkIn = CatalogSeeding.Today().AddDays(daysUntilCheckIn);

        HoldAvailabilityResponse? hold = await (await factory.CreateClient().PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken)).Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(hold);
        return hold.HoldId;
    }

    [Fact]
    public async Task ABookingInsertWhoseCommitLosesItsAcknowledgement_ReturnsTheBookingAndAWorkingToken()
    {
        Guid holdId = await HoldAUnitAsync(daysUntilCheckIn: 150);

        // After the confirmation's one commit, which claims the hold and inserts
        // the booking together.
        CommitFault<AppDbContext> lostAck = CommitFaults.FailAfterCommit<AppDbContext>(context =>
            context.ChangeTracker.Entries<Booking>().Any(e => e.Entity.HoldId == holdId));

        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);

        // Act
        HttpResponseMessage response = await host.CreateClient().PostAsJsonAsync("/api/bookings",
            new ConfirmBookingRequest { HoldId = holdId, GuestName = "Jane Guest", GuestEmail = "jane@example.com" },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the booking insert.");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ConfirmBookingResponse? confirmed = await response.Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(confirmed?.ManagementToken);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            BookingsDb db = scope.ServiceProvider.GetRequiredService<BookingsDb>();

            // One booking, the one returned; the hold claimed.
            Booking booking = Assert.Single(await db.Bookings.AsNoTracking()
                .Where(b => b.HoldId == holdId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(confirmed.BookingId, booking.Id);

            Assert.Equal("pending_payment", (await db.UnitAvailabilityHolds.AsNoTracking()
                .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken)).Status);

            Assert.Single(await db.BookingManagementTokens.AsNoTracking()
                .Where(t => t.BookingId == booking.Id).ToListAsync(TestContext.Current.CancellationToken));
        }

        // The one that matters: the token opens a session.
        HttpResponseMessage exchange = await factory.CreateClient().PostAsJsonAsync(
            $"/api/bookings/{confirmed.BookingId}/manage/session",
            new CreateBookingSessionRequest { BookingId = confirmed.BookingId, ManagementToken = confirmed.ManagementToken },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
    }

    [Fact]
    public async Task ADiscountedConfirmationWhoseCommitLosesItsAcknowledgement_CountsItsRedemptionOnce()
    {
        // The retry re-runs the whole scope against a database that already holds
        // this checkout's redemption. It must return the booking rather than meet
        // its own redemption on the one-per-email index, and must not count a
        // second slot.
        Promotion promotion = Promotion.CreatePlatformPromotion(
            Guid.CreateVersion7(),
            $"RETRY{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            PromotionDiscountType.Percentage,
            10m,
            null,
            expiresAt: null,
            maxRedemptions: null,
            hostId: null);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            PromotionsDb promotions = scope.ServiceProvider.GetRequiredService<PromotionsDb>();
            promotions.Add(promotion);
            await promotions.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Guid holdId = await HoldAUnitAsync(daysUntilCheckIn: 151);

        CommitFault<AppDbContext> lostAck = CommitFaults.FailAfterCommit<AppDbContext>(context =>
            context.ChangeTracker.Entries<Booking>().Any(e => e.Entity.HoldId == holdId));

        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);

        HttpResponseMessage response = await host.CreateClient().PostAsJsonAsync("/api/bookings",
            new ConfirmBookingRequest
            {
                HoldId = holdId,
                GuestName = "Jane Guest",
                GuestEmail = "jane@example.com",
                PromoCode = promotion.Code
            },
            TestContext.Current.CancellationToken);

        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the confirmation.");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ConfirmBookingResponse? confirmed = await response.Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(confirmed?.ManagementToken);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            BookingsDb bookings = scope.ServiceProvider.GetRequiredService<BookingsDb>();
            Booking booking = Assert.Single(await bookings.Bookings.AsNoTracking()
                .Where(b => b.HoldId == holdId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(confirmed.BookingId, booking.Id);

            PromotionsDb promotions = scope.ServiceProvider.GetRequiredService<PromotionsDb>();
            Assert.Single(await promotions.PromotionRedemptions.AsNoTracking()
                .Where(r => r.BookingId == booking.Id).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, (await promotions.Promotions.AsNoTracking()
                .SingleAsync(p => p.Id == promotion.Id, TestContext.Current.CancellationToken)).RedemptionCount);
        }

        HttpResponseMessage exchange = await factory.CreateClient().PostAsJsonAsync(
            $"/api/bookings/{confirmed.BookingId}/manage/session",
            new CreateBookingSessionRequest { BookingId = confirmed.BookingId, ManagementToken = confirmed.ManagementToken },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
    }
}
