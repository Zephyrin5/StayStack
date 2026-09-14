// AUDIT 2026-09-14: Injects through CommitFaults.FailBeforeCommit - pre-commit, the rollback case, as named; targeted by the tracked booking; asserts through a fresh scope. Probed: fails without ChangeTracker.Clear(). Gap: nothing injects after the commit, so the handler's already-Cancelled recovery branch never runs.
using Bookings;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.CreateBookingSession;
using Bookings.Features.HoldAvailability;
using Catalog;
using Catalog.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Bookings;

// A transient failure on COMMIT is the one failure an execution strategy
// exists to absorb, and it is the one that exposes work built outside the
// retried delegate: SaveChangesAsync defaults to acceptAllChangesOnSuccess,
// so by the time it returns, every mutation and every enqueued outbox row is
// already Unchanged. A retry then saves nothing at all - while the statements
// that are not EF's, like the hold release, run again quite happily.
[Collection("Integration Tests")]
public class CancelRetryTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly List<Property> _pendingProperties = [];

    // Before the commit carrying this booking: the rollback case, where nothing
    // was written and the retry must write it. Targeted at the tracked booking
    // because TickerQ's jobs commit on this context too.
    private static CommitFault<AppBookingsDbContext> FailTheCommitCarrying(Guid bookingId) =>
        CommitFaults.FailBeforeCommit<AppBookingsDbContext>(context =>
            context.ChangeTracker.Entries<Booking>().Any(entry => entry.Entity.Id == bookingId));

    private Unit CreateTestUnit()
    {
        Property property = CatalogSeeding.CreateProperty();
        _pendingProperties.Add(property);

        return Unit.Create(
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            100m);
    }

    // Seeded through the base factory so the interceptor below only ever sees
    // the cancellation's own commit.
    private async Task<(Guid BookingId, string ManagementToken, Guid HoldId)> CreateGuestBookingAsync()
    {
        Unit unit = CreateTestUnit();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
            context.AddRange(_pendingProperties);
            _pendingProperties.Clear();
            context.Add(unit);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        HttpClient client = factory.CreateClient();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(30);

        HttpResponseMessage holdResponse = await client.PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken);
        HoldAvailabilityResponse? hold = await holdResponse.Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);

        HttpResponseMessage confirmResponse = await client.PostAsJsonAsync("/api/bookings", new ConfirmBookingRequest
        {
            HoldId = hold.HoldId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        }, TestContext.Current.CancellationToken);
        ConfirmBookingResponse? booking = await confirmResponse.Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(booking?.ManagementToken);
        return (booking.BookingId, booking.ManagementToken, hold.HoldId);
    }

    [Fact]
    public async Task ACommitThatFailsOnceAndRetries_StillCancelsTheBooking()
    {
        (Guid bookingId, string managementToken, Guid holdId) = await CreateGuestBookingAsync();

        CommitFault<AppBookingsDbContext> commitFailure = FailTheCommitCarrying(bookingId);
        using WebApplicationFactory<Program> host = factory.WithCommitFault(commitFailure);

        using HttpClient client = host.CreateClient();

        // The session has to be opened on this host too - it is the one whose
        // commits the interceptor watches, and the exchange is a read so it
        // commits nothing.
        HttpResponseMessage exchange = await client.PostAsJsonAsync(
            $"/api/bookings/{bookingId}/manage/session",
            new CreateBookingSessionRequest
            {
                BookingId = bookingId,
                ManagementToken = managementToken
            }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        CreateBookingSessionResponse? session = await exchange.Content
            .ReadFromJsonAsync<CreateBookingSessionResponse>(
                TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(session);

        HttpRequestMessage cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{bookingId}/cancel")
        {
            Content = JsonContent.Create(new { bookingId, guestEmail = "jane@example.com" })
        };
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.SessionToken);

        HttpResponseMessage response = await client.SendAsync(cancel, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Without this the whole test is vacuous: a failure that never fired,
        // or fired on some background job's transaction instead, leaves every
        // assertion below passing for the wrong reason.
        Assert.True(commitFailure.HasFired, "The commit failure never reached the cancellation.");

        // Asserted through a fresh scope, never the context the request used.
        // A handler that accepted its changes and then failed to commit leaves
        // an in-memory entity that reads exactly like success.
        using IServiceScope assertScope = factory.Services.CreateScope();
        AppBookingsDbContext db = assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        Booking booking = await db.Bookings.AsNoTracking()
            .SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken);
        Assert.Equal(BookingStatus.Cancelled, booking.BookingStatus);

        UnitAvailabilityHold hold = await db.UnitAvailabilityHolds.AsNoTracking()
            .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken);
        Assert.Equal("held", hold.Status);

        // The compensations must be durable rows. The response says a refund
        // is pending on the strength of them existing; without them the relay
        // backstop has nothing to deliver and the promise is empty.
        int compensations = await db.BookingsOutboxMessages.AsNoTracking()
            .CountAsync(m => m.Payload.Contains(bookingId.ToString()), TestContext.Current.CancellationToken);
        Assert.Equal(2, compensations);
    }
}
