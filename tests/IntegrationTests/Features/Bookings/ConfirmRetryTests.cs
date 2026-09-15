// Proves a confirmation whose booking-insert commit loses its acknowledgement (FailAfterCommit)
// returns the committed booking. With the read-back recovery disabled it answers 500; a recovery
// returning a different management token is caught only by the session exchange assertion.
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
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Bookings;

// ConfirmBookingHandler's recovery for a booking insert that committed and lost
// its acknowledgement - reasoned about over several rounds, and never run until
// this test.
//
// The booking id and the management token's plaintext are chosen before the
// insert, so a retry that collides on the booking's primary key can read the
// committed booking back and answer with it. The assertion that matters is the
// token: an earlier version of this recovery returned a 200 and a real booking
// id with a management token whose hash row had been rolled back, a credential
// that failed at first use. A test asserting the status and the id passed over
// that. This one uses the token.
[Collection("Integration Tests")]
public class ConfirmRetryTests(IntegrationTestWebApplicationFactory factory)
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
            AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
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

        // After the commit that inserts the booking for this hold - the second of
        // the confirmation's two commits, not the one that claims the hold.
        CommitFault<AppBookingsDbContext> lostAck = CommitFaults.FailAfterCommit<AppBookingsDbContext>(context =>
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
            AppBookingsDbContext db = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

            // One booking, the one returned; its intent gone; the hold claimed.
            Booking booking = Assert.Single(await db.Bookings.AsNoTracking()
                .Where(b => b.HoldId == holdId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(confirmed.BookingId, booking.Id);

            Assert.False(await db.PendingBookingIntents.AsNoTracking()
                .AnyAsync(i => i.HoldId == holdId, TestContext.Current.CancellationToken));

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
}
