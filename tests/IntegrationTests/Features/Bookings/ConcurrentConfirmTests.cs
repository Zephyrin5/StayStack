using Bookings;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.HoldAvailability;
using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Bookings;

// Which arbiter decides a race for one hold.
//
// ExecuteConfirmAsync is a conditional UPDATE - WHERE status = 'held' - so it
// is already an exactly-one-winner compare-and-set on the contended row. It
// only gets to act as one if it runs before the intent insert; with the insert
// first, the intent's unique index on hold_id decided races instead, and the
// loser was told "a confirmation for this hold is already in progress" after a
// recovery path dated another request's intent against a grace period to work
// out which of three situations it was in.
[Collection("Integration Tests")]
public class ConcurrentConfirmTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly List<Property> _pendingProperties = [];

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

    private async Task<Guid> SeedHeldUnitAsync(int daysUntilCheckIn)
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

        DateOnly checkIn = CatalogSeeding.Today().AddDays(daysUntilCheckIn);

        HttpResponseMessage response = await factory.CreateClient().PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken);

        HoldAvailabilityResponse? hold = await response.Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);
        return hold.HoldId;
    }

    private static ConfirmBookingRequest RequestFor(Guid holdId) => new ConfirmBookingRequest
    {
        HoldId = holdId,
        GuestName = "Jane Guest",
        GuestEmail = "jane@example.com"
    };

    private Task<HttpResponseMessage> ConfirmAsync(ConfirmBookingRequest request, string? idempotencyKey)
    {
        HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(request)
        };

        if (idempotencyKey is not null)
        {
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        // A fresh client per attempt so the two genuinely race rather than
        // queueing behind one connection.
        return factory.CreateClient().SendAsync(message, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TwoConfirmationsForOneHold_LeaveExactlyOneBooking()
    {
        Guid holdId = await SeedHeldUnitAsync(daysUntilCheckIn: 9);
        ConfirmBookingRequest request = RequestFor(holdId);

        HttpResponseMessage[] responses = await Task.WhenAll(
            ConfirmAsync(request, idempotencyKey: null),
            ConfirmAsync(request, idempotencyKey: null));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));

        // The loser is told the hold is gone, which is what actually happened
        // and what a caller can act on - the same answer an expired hold gets,
        // because the remedy is identical. It used to be a 409 naming another
        // request's confirmation, which is a fact about someone else.
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NotFound));

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookings = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        Assert.Equal(1, await bookings.Bookings.AsNoTracking()
            .CountAsync(b => b.HoldId == holdId, TestContext.Current.CancellationToken));

        // And the loser left nothing behind for the reconcile job to find.
        Assert.Equal(0, await bookings.PendingBookingIntents.AsNoTracking()
            .CountAsync(i => i.HoldId == holdId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TwoConfirmationsUnderOneIdempotencyKey_DoNotReport404()
    {
        // The guarantee that had to survive moving the arbiter. Both attempts
        // are the same logical checkout, so the loser must be answered as a
        // repeat - replayed once the winner finishes, or told it is still in
        // progress - never "that hold is gone", which would report failure for
        // a checkout that succeeded. See docs/adr/0022.
        Guid holdId = await SeedHeldUnitAsync(daysUntilCheckIn: 16);
        ConfirmBookingRequest request = RequestFor(holdId);
        string key = Guid.NewGuid().ToString();

        HttpResponseMessage[] responses = await Task.WhenAll(
            ConfirmAsync(request, key),
            ConfirmAsync(request, key));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));

        HttpResponseMessage other = responses.Single(r => r.StatusCode != HttpStatusCode.OK);

        Assert.NotEqual(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
    }

    [Fact]
    public async Task AConfirmationAfterTheHoldIsTaken_IsToldTheHoldIsGone()
    {
        // The sequential version, which used to take a different path through
        // the recovery than the concurrent one and produce different wording
        // depending on how old the other request's intent was. One answer now.
        Guid holdId = await SeedHeldUnitAsync(daysUntilCheckIn: 23);
        ConfirmBookingRequest request = RequestFor(holdId);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(request, idempotencyKey: null)).StatusCode);

        HttpResponseMessage second = await ConfirmAsync(request, idempotencyKey: null);

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }
}
