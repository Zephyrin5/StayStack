using Bookings;
using Bookings.Entities;
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

// A confirmation's plaintext management token exists only in the response -
// the database keeps a hash. So a response the client never receives strands
// an anonymous guest with a booking they cannot reach, cancel, or prove is
// theirs, and their retry fails on the hold they already consumed. These
// cover the Idempotency-Key header that makes that retry work, and the two
// guards that stop it becoming a way into somebody else's booking.
[Collection("Integration Tests")]
public class CheckoutIdempotencyTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly List<Property> _pendingProperties = [];
    private readonly HttpClient _client = factory.CreateClient();

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

    private async Task SeedCatalogAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        context.AddRange(_pendingProperties);
        _pendingProperties.Clear();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private async Task<Guid> HoldUnitAsync(Guid unitId, int daysUntilCheckIn = 5)
    {
        DateOnly checkIn = CatalogSeeding.Today().AddDays(daysUntilCheckIn);

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
        {
            UnitId = unitId,
            CheckIn = checkIn,
            CheckOut = checkIn.AddDays(3),
            GuestCount = 2
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HoldAvailabilityResponse? hold = await response.Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);
        return hold.HoldId;
    }

    // The header is assigned by the endpoint after model binding, so it has to
    // be sent as a header - putting it in the body would silently do nothing,
    // which is itself worth having these go through the real request pipeline.
    private async Task<HttpResponseMessage> ConfirmAsync(
        Guid holdId, string? idempotencyKey, string guestName = "Jane Guest", string guestEmail = "jane@example.com")
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(new ConfirmBookingRequest
            {
                HoldId = holdId,
                GuestName = guestName,
                GuestEmail = guestEmail
            })
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<ConfirmBookingResponse> ReadAsync(HttpResponseMessage response)
    {
        ConfirmBookingResponse? body = await response.Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    [Fact]
    public async Task ARetryWithTheSameKey_ReturnsTheOriginalBookingAndItsManagementToken()
    {
        // The motivating case. From the server's side a response that never
        // arrived is indistinguishable from one that did, so replaying the
        // key is exactly what a client recovering from a dropped connection
        // does - and this asserts it gets back the token, which is the only
        // part that cannot be recovered any other way.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);
        string key = Guid.NewGuid().ToString();

        HttpResponseMessage first = await ConfirmAsync(holdId, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        ConfirmBookingResponse original = await ReadAsync(first);
        Assert.NotNull(original.ManagementToken);

        HttpResponseMessage second = await ConfirmAsync(holdId, key);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        ConfirmBookingResponse replayed = await ReadAsync(second);
        Assert.Equal(original.BookingId, replayed.BookingId);
        Assert.Equal(original.ManagementToken, replayed.ManagementToken);

        // And the retry created nothing. Without the key this second call
        // returns 404 on the consumed hold; the failure mode being fixed is
        // not a double booking, it is a guest locked out of a real one.
        using IServiceScope scope = factory.Services.CreateScope();
        int bookingsForHold = await scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>()
            .Bookings.AsNoTracking()
            .CountAsync(b => b.HoldId == holdId, TestContext.Current.CancellationToken);
        Assert.Equal(1, bookingsForHold);
    }

    [Fact]
    public async Task ARetryWithoutAKey_CannotReachTheBooking()
    {
        // The behaviour the header exists to fix, pinned so it stays fixed
        // only where a key was actually sent. The hold is consumed, so the
        // retry fails its status = 'held' guard - and the guest has no
        // booking id and no token, because both existed only in the response
        // they never saw.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(holdId, idempotencyKey: null)).StatusCode);

        HttpResponseMessage retry = await ConfirmAsync(holdId, idempotencyKey: null);

        Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
    }

    [Fact]
    public async Task AKeyReplayedWithADifferentRequest_IsRefused()
    {
        // Both a correctness guard and the security one. Replay hands back a
        // live management token, so without the fingerprint check a guessed
        // key would be a way into somebody else's booking; with it, an
        // attacker must already know the hold id and the guest's name and
        // email, at which point the key tells them nothing.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);
        string key = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(holdId, key)).StatusCode);

        HttpResponseMessage mismatched = await ConfirmAsync(holdId, key, guestEmail: "someone.else@example.com");

        Assert.Equal(HttpStatusCode.Conflict, mismatched.StatusCode);

        // And it leaked nothing: the refusal must not carry the booking it
        // declined to replay.
        string body = await mismatched.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("managementToken", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TwoDifferentKeysOnTheSameHold_DoNotBothBook()
    {
        // A fresh key is not a way around the hold's own single-use guard.
        // The second call is a different checkout as far as idempotency is
        // concerned, so it proceeds - and then fails on the consumed hold,
        // which is correct.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(holdId, Guid.NewGuid().ToString())).StatusCode);

        HttpResponseMessage second = await ConfirmAsync(holdId, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        int bookingsForHold = await scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>()
            .Bookings.AsNoTracking()
            .CountAsync(b => b.HoldId == holdId, TestContext.Current.CancellationToken);
        Assert.Equal(1, bookingsForHold);
    }

    [Fact]
    public async Task AFailedConfirmation_FreesItsKeyForTheRetry()
    {
        // The reservation is taken in the same transaction as the hold
        // transition, so a confirmation that never happened must not burn the
        // key it carried. Here the first attempt fails on a hold that does
        // not exist; the same key then has to work on a real one, or the
        // client's retry - the entire point of sending a key - is refused.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string key = Guid.NewGuid().ToString();

        HttpResponseMessage failed = await ConfirmAsync(Guid.CreateVersion7(), key);
        Assert.Equal(HttpStatusCode.NotFound, failed.StatusCode);

        Guid holdId = await HoldUnitAsync(unit.Id);
        HttpResponseMessage retried = await ConfirmAsync(holdId, key);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
    }

    [Fact]
    public async Task ACompletedRecord_CarriesTheTokenAndTheBookingItReplays()
    {
        // The storage-shape assertion behind the replay: the record must be
        // completed in the same transaction as the booking, or a committed
        // booking whose record never landed strands the guest exactly as
        // before - the bug reintroduced one level up.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);
        string key = Guid.NewGuid().ToString();

        ConfirmBookingResponse created = await ReadAsync(await ConfirmAsync(holdId, key));

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        CheckoutIdempotencyRecord record = await context.CheckoutIdempotencyRecords.AsNoTracking()
            .SingleAsync(r => r.BookingId == created.BookingId, TestContext.Current.CancellationToken);

        Assert.NotNull(record.CompletedAt);
        Assert.Equal(created.ManagementToken, record.ManagementToken);

        // The key itself is never stored - only its hash - so a database
        // reader cannot replay other people's checkouts.
        Assert.DoesNotContain(key, record.KeyHash, StringComparison.Ordinal);

        // And the intent is gone, as it always was: the two rows have
        // deliberately different endings.
        Assert.False(await context.PendingBookingIntents.AsNoTracking()
            .AnyAsync(i => i.Id == created.BookingId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnExpiredRecord_IsRefusedEvenIfThePurgeNeverRan()
    {
        // The window used to live only in PurgeReplayedCheckoutsJob's DELETE,
        // which makes it a property of a background job rather than of the
        // system: stop that job, break its cron, or let it fail quietly, and
        // replay kept working forever, handing back a management token of
        // unbounded age. This ages the record in place, deliberately without
        // running the purge, so it fails if enforcement ever moves back out of
        // the request path.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);
        string key = Guid.NewGuid().ToString();

        ConfirmBookingResponse created = await ReadAsync(await ConfirmAsync(holdId, key));

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

            await context.CheckoutIdempotencyRecords
                .Where(r => r.BookingId == created.BookingId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(r => r.CreatedAt, DateTimeOffset.UtcNow.AddDays(-8)),
                    TestContext.Current.CancellationToken);
        }

        HttpResponseMessage replayed = await ConfirmAsync(holdId, key);

        Assert.Equal(HttpStatusCode.Conflict, replayed.StatusCode);

        // And it says nothing about the booking it declined to replay.
        string body = await replayed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("managementToken", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AShortKey_IsRejectedAtTheBoundary()
    {
        // A caller sending a counter has misunderstood what the key is for,
        // and two guests colliding on "1" would be the worst possible way to
        // find out - one of them would be handed the other's booking.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);

        HttpResponseMessage response = await ConfirmAsync(holdId, idempotencyKey: "1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
