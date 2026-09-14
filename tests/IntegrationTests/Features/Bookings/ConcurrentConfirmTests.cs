// AUDIT 2026-09-14: Pauses checked against ConfirmBookingHandler: ConfirmHoldAsync (:605) runs before the first commit and GetUnitAsync (:332) after it, as each test claims. TwoConfirmationsForOneHold is an unforced race. Fresh-scope asserts. Not mutation-probed. The lost acknowledgement of ConfirmBookingHandler's booking-insert commit is ConfirmRetryTests'.
using Bookings;
using Bookings.Contracts;
using Bookings.Features.CreateBookingSession;
using Catalog.Contracts;
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
            Guid.CreateVersion7(),
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

    private Task<HttpResponseMessage> SendConfirmAsync(
        HttpClient client, ConfirmBookingRequest request, string idempotencyKey)
    {
        HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);

        return client.SendAsync(message, TestContext.Current.CancellationToken);
    }

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

    // The two halves of what used to be one test asserting a scheduler
    // outcome.
    //
    // TwoConfirmationsUnderOneIdempotencyKey_DoNotReport404 fired both requests
    // with Task.WhenAll and demanded exactly one 200 and one 409. Nothing makes
    // both attempts reach the in-progress state: if the second arrives after
    // the first has finished, it observes a completed record and replays, which
    // is a perfectly correct double-200 and failed the assertion. Worse, it
    // meant the completed-replay path - the one carrying the rolled-back-token
    // defect - was only exercised when the scheduler happened to produce it,
    // and the test asserted that outcome was wrong.
    //
    // Both cases are real and both are pinned below, each driven from a barrier
    // rather than from whoever wins.

    // Pauses a confirmation *before* it attempts the hold - after its
    // top-of-handler idempotency read has already missed.
    //
    // The pause is deliberately on the second request rather than the first.
    // Holding the first one open with its transaction still live also works to
    // make the second miss its read, but the second then unblocks the instant
    // the first commits its *first* transaction - so it finds the reservation
    // with completed_at still null and is correctly told "in progress", which
    // is the other test. Pausing the reader instead lets the winner finish
    // entirely, which is the only way to reach the completed-replay branch
    // deterministically.
    private sealed class PauseBeforeConfirmingHold(
        IHoldConfirmation inner, TaskCompletionSource gate, TaskCompletionSource reached) : IHoldConfirmation
    {
        public async Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken)
        {
            reached.TrySetResult();
            await gate.Task;
            return await inner.ConfirmHoldAsync(holdId, cancellationToken);
        }

        public Task<ConfirmedHold?> GetConfirmedHoldAsync(Guid holdId, CancellationToken cancellationToken) =>
            inner.GetConfirmedHoldAsync(holdId, cancellationToken);

        public Task<bool> MarkHoldPaidAsync(Guid holdId, CancellationToken cancellationToken) =>
            inner.MarkHoldPaidAsync(holdId, cancellationToken);

        public Task ReleaseHoldAsync(Guid holdId, CancellationToken cancellationToken) =>
            inner.ReleaseHoldAsync(holdId, cancellationToken);
    }

    // Pauses the first confirmation *after* its first transaction has
    // committed, so its idempotency record exists with completed_at still null
    // - the only window in which "still in progress" is the right answer.
    private sealed class PauseBeforeBuildingTheBooking(
        IUnitLookup inner, TaskCompletionSource gate, TaskCompletionSource reached) : IUnitLookup
    {
        public async Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken)
        {
            UnitSummary? unit = await inner.GetUnitAsync(unitId, cancellationToken);
            reached.TrySetResult();
            await gate.Task;
            return unit;
        }

        public Task<IReadOnlyDictionary<Guid, UnitSummary>> GetUnitsAsync(
            IEnumerable<Guid> unitIds, CancellationToken cancellationToken) =>
            inner.GetUnitsAsync(unitIds, cancellationToken);

        public Task<UnitSummary?> GetUnitIncludingArchivedAsync(Guid unitId, CancellationToken cancellationToken) =>
            inner.GetUnitIncludingArchivedAsync(unitId, cancellationToken);

        public Task<IReadOnlyList<Guid>> GetUnitIdsForHostAsync(Guid hostId, CancellationToken cancellationToken) =>
            inner.GetUnitIdsForHostAsync(hostId, cancellationToken);

        public Task<StayPricingResult?> ResolveStayPricingAsync(
            Guid unitId, DateOnly checkIn, DateOnly checkOut, CancellationToken cancellationToken) =>
            inner.ResolveStayPricingAsync(unitId, checkIn, checkOut, cancellationToken);
    }

    private static (TaskCompletionSource Gate, TaskCompletionSource Reached) NewGate() =>
        (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    [Fact]
    public async Task ASecondConfirmationWhileTheFirstIsStillRunning_IsToldItIsInProgress()
    {
        // Never 404. Both attempts are the same logical checkout, so the loser
        // must be answered as a repeat - not "that hold is gone", which reports
        // failure for a checkout that is succeeding. See docs/adr/0022.
        Guid holdId = await SeedHeldUnitAsync(daysUntilCheckIn: 16);
        ConfirmBookingRequest request = RequestFor(holdId);
        string key = Guid.NewGuid().ToString();

        (TaskCompletionSource gate, TaskCompletionSource reached) = NewGate();

        HttpClient paused = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IUnitLookup));
                services.Remove(original);
                services.AddScoped<IUnitLookup>(sp => new PauseBeforeBuildingTheBooking(
                    (IUnitLookup)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), gate, reached));
            })).CreateClient();

        Task<HttpResponseMessage> first = SendConfirmAsync(paused, request, key);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The reservation is committed with completed_at null. This is the
        // whole window the "in progress" answer exists for.
        HttpResponseMessage second = await ConfirmAsync(request, key);

        Assert.NotEqual(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        gate.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
    }

    [Fact]
    public async Task AConfirmationThatArrivesBeforeTheWinnerCommits_ReplaysAUsableToken()
    {
        // The completed-replay case, and the regression test for a token that
        // was minted inside a transaction nobody committed.
        //
        // The replaying request has to pass its top-of-handler idempotency read
        // *before* the winner commits, or it replays from there - a path with
        // no transaction at all, where the defect cannot appear. So it is the
        // one that gets paused, parked after that read has missed and before it
        // touches the hold, while the winner runs to completion beside it.
        //
        // Released, it walks into the recovery: the hold is gone, the intent
        // under its own booking id does not exist, and the key resolves to a
        // completed record. That is the branch that used to call ReplayAsync
        // with the caller's transaction still open, returning a 200 for a real
        // booking with a management token whose hash row was rolled back on the
        // way out - a credential that fails at first use.
        Guid holdId = await SeedHeldUnitAsync(daysUntilCheckIn: 17);
        ConfirmBookingRequest request = RequestFor(holdId);
        string key = Guid.NewGuid().ToString();

        (TaskCompletionSource gate, TaskCompletionSource reached) = NewGate();

        HttpClient paused = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IHoldConfirmation));
                services.Remove(original);
                services.AddScoped<IHoldConfirmation>(sp => new PauseBeforeConfirmingHold(
                    (IHoldConfirmation)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), gate, reached));
            })).CreateClient();

        // The replayer starts first and parks with its read already missed.
        Task<HttpResponseMessage> second = SendConfirmAsync(paused, request, key);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The winner, start to finish, while the other is held.
        HttpResponseMessage winner = await ConfirmAsync(request, key);

        gate.SetResult();

        HttpResponseMessage replayed = await second;

        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);

        ConfirmBookingResponse? original = await winner.Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        ConfirmBookingResponse? repeat = await replayed.Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(original);
        Assert.NotNull(repeat);
        Assert.Equal(original.BookingId, repeat.BookingId);
        Assert.NotNull(repeat.ManagementToken);

        // The assertion that would have caught it. A test stopping at the 200
        // and the booking id passes against the old code.
        HttpResponseMessage exchange = await factory.CreateClient().PostAsJsonAsync(
            $"/api/bookings/{repeat.BookingId}/manage/session",
            new CreateBookingSessionRequest
            {
                BookingId = repeat.BookingId,
                ManagementToken = repeat.ManagementToken!
            }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);

        // And the winner's own token still works - the replay adds a token
        // rather than rotating one.
        HttpResponseMessage originalExchange = await factory.CreateClient().PostAsJsonAsync(
            $"/api/bookings/{original.BookingId}/manage/session",
            new CreateBookingSessionRequest
            {
                BookingId = original.BookingId,
                ManagementToken = original.ManagementToken!
            }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, originalExchange.StatusCode);
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
