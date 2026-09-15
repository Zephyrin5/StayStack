// Proves two confirmations for one hold leave exactly one booking (an unforced race), and that
// a second confirmation under the same idempotency key replays the first's booking with a usable
// token, whether it waits on the first's open transaction or arrives after the first commits; each
// interleaving is pinned by a barrier. Not verified by breaking the mechanisms.
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
using Npgsql;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Bookings;

// Which arbiter decides a race for one hold.
//
// ConfirmHoldAsync is a conditional UPDATE - WHERE status = 'held' - so it is an
// exactly-one-winner compare-and-set on the contended row. A loser that reaches
// it while the winner's transaction is open waits on the row lock, then matches
// nothing once the winner commits.
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

        // The loser is told the hold is gone, which is what happened and what a
        // caller can act on - the same answer an expired hold gets, because the
        // remedy is identical.
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NotFound));

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookings = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        Assert.Equal(1, await bookings.Bookings.AsNoTracking()
            .CountAsync(b => b.HoldId == holdId, TestContext.Current.CancellationToken));
    }

    // Two confirmations under one idempotency key, split into two tests
    // because firing both with Task.WhenAll asserts a scheduler outcome: if the
    // second arrives after the first finishes, it replays a completed record
    // (a correct double 200), and the completed-replay path is exercised only
    // when the scheduler happens to produce it. Each case below is driven from a
    // barrier rather than from whoever wins.

    // Pauses a confirmation *before* it attempts the hold - after its
    // top-of-handler idempotency read has already missed.
    //
    // The pause is on the second request rather than the first, so the winner
    // commits before the second reaches the hold at all. Pausing the winner
    // instead is the other test: the second then waits on its row lock.
    private sealed class PauseBeforeConfirmingHold(
        IHoldConfirmation inner, TaskCompletionSource gate, TaskCompletionSource reached) : IHoldConfirmation
    {
        public async Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken)
        {
            reached.TrySetResult();
            await gate.Task;
            return await inner.ConfirmHoldAsync(holdId, cancellationToken);
        }

        public Task<bool> MarkHoldPaidAsync(Guid holdId, CancellationToken cancellationToken) =>
            inner.MarkHoldPaidAsync(holdId, cancellationToken);

        public Task ReleaseHoldAsync(Guid holdId, CancellationToken cancellationToken) =>
            inner.ReleaseHoldAsync(holdId, cancellationToken);
    }

    // Pauses the first confirmation after it has claimed the hold, with its
    // transaction still open.
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
    public async Task ASecondConfirmationWhileTheFirstIsStillRunning_WaitsForItAndReplaysItsBooking()
    {
        // Never 404. Both attempts are the same logical checkout, so the second
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

        // The first holds the hold's row lock with nothing committed, so the
        // second misses its idempotency read and blocks in ConfirmHoldAsync.
        Task<HttpResponseMessage> second = ConfirmAsync(request, key);
        await WaitUntilAHoldTransitionIsWaitingOnALockAsync();

        gate.SetResult();

        HttpResponseMessage winner = await first;
        HttpResponseMessage repeat = await second;

        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);

        ConfirmBookingResponse? original = await winner.Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        ConfirmBookingResponse? replayed = await repeat.Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(original);
        Assert.NotNull(replayed);
        Assert.Equal(original.BookingId, replayed.BookingId);

        HttpResponseMessage exchange = await factory.CreateClient().PostAsJsonAsync(
            $"/api/bookings/{replayed.BookingId}/manage/session",
            new CreateBookingSessionRequest
            {
                BookingId = replayed.BookingId,
                ManagementToken = replayed.ManagementToken!
            }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
    }

    // The barrier for the blocked side: Postgres reports the waiting statement,
    // so the test proceeds only once the second request is parked on the
    // first's row lock rather than after a guessed delay.
    private async Task WaitUntilAHoldTransitionIsWaitingOnALockAsync()
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        while (true)
        {
            await using NpgsqlCommand command = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE wait_event_type = 'Lock' AND query LIKE '%UPDATE unit_availability_holds%')
                """, connection);

            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    [Fact]
    public async Task AConfirmationThatArrivesBeforeTheWinnerCommits_ReplaysAUsableToken()
    {
        // The completed-replay case: a replay must not hand out a token minted
        // inside a transaction nobody committed.
        //
        // The replaying request has to pass its top-of-handler idempotency read
        // *before* the winner commits, or it replays from there - a path with
        // no transaction at all. So it is the one that gets paused, parked after
        // that read has missed and before it touches the hold, while the winner
        // runs to completion beside it.
        //
        // Released, it finds the hold gone and the key resolved to a completed
        // record. Replaying with its own transaction still open would return a
        // 200 and a real booking with a management token whose hash row rolls
        // back on the way out - a credential that fails at first use.
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

        // The token must work. A test stopping at the 200 and the booking id
        // cannot see a rolled-back token hash.
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
        // The sequential version must get the same answer as the concurrent
        // one, whatever the age of the other request's intent.
        Guid holdId = await SeedHeldUnitAsync(daysUntilCheckIn: 23);
        ConfirmBookingRequest request = RequestFor(holdId);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(request, idempotencyKey: null)).StatusCode);

        HttpResponseMessage second = await ConfirmAsync(request, idempotencyKey: null);

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }
}
