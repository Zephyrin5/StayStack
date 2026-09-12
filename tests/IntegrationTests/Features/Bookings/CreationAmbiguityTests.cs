using Bookings;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.CreateBookingSession;
using Bookings.Features.HoldAvailability;
using BuildingBlocks.Identity;
using Catalog;
using Catalog.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Persistence.Interceptors;
using SeedWork.ValueObjects;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Transactions;
using Transactions.Entities;
using Transactions.Features.InitiateTransaction;
namespace IntegrationTests.Features.Bookings;

// A creation path cannot recover from an ambiguous commit unless it knows what
// identity it used. EnableRetryOnFailure cannot distinguish a failed
// transaction from one that committed and lost its acknowledgement, so it
// re-runs the delegate either way - and a delegate that mints a fresh id on
// each attempt collides with its own predecessor and reports that collision as
// somebody else's conflict.
//
// ConfirmBookingHandler already pre-generates its booking id and reads back on
// conflict. These are the other two call sites.
[Collection("Integration Tests")]
public class CreationAmbiguityTests(IntegrationTestWebApplicationFactory factory)
{
    // Lets the commit land and then throws, which is what a dropped
    // acknowledgement looks like from inside the process. Armed for one unit so
    // TickerQ's own commits cannot steal it.
    private sealed class LoseTheAckOnFirstBookingsCommitFor(ICurrentUserProvider currentUser, TimeProvider timeProvider)
        : AuditableEntitySaveChangesInterceptor(currentUser, timeProvider), IDbTransactionInterceptor
    {
        private static int _armed;
        private static int _fired;

        public static void Arm()
        {
            Interlocked.Exchange(ref _armed, 1);
            Interlocked.Exchange(ref _fired, 0);
        }

        public static void Disarm() => Interlocked.Exchange(ref _armed, 0);

        public static bool Fired => Volatile.Read(ref _fired) > 0;

        public Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && eventData.Context is AppBookingsDbContext
                && Interlocked.Increment(ref _fired) == 1)
            {
                throw new PostgresException(
                    "simulated lost acknowledgement after commit", "ERROR", "ERROR", "40001");
            }

            return Task.CompletedTask;
        }
    }

    // Hooks the command, not the transaction and not SavedChangesAsync, and
    // both rejected alternatives are worth recording.
    //
    // InitiateTransactionHandler opens no explicit transaction - EF wraps its
    // single SaveChanges in the implicit one - so no transaction interceptor
    // fires there at all, and the first draft of this test simply never
    // triggered. SavedChangesAsync does fire, but it runs *outside* the region
    // the execution strategy retries, so throwing there propagated a 500
    // instead of provoking a second attempt: the simulation has to sit where a
    // real lost acknowledgement sits, inside the retried work.
    //
    // ReaderExecutedAsync/NonQueryExecutedAsync run immediately after the
    // INSERT has executed and inside that region, which is exactly the shape:
    // the row is in the database and the caller is about to be told it is not.
    private sealed class LoseTheAckOnFirstTransactionsCommit(ICurrentUserProvider currentUser, TimeProvider timeProvider)
        : AuditableEntitySaveChangesInterceptor(currentUser, timeProvider), IDbCommandInterceptor
    {
        private static int _armed;
        private static int _fired;

        public static void Arm()
        {
            Interlocked.Exchange(ref _armed, 1);
            Interlocked.Exchange(ref _fired, 0);
        }

        public static void Disarm() => Interlocked.Exchange(ref _armed, 0);

        public static bool Fired => Volatile.Read(ref _fired) > 0;

        public ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Throw(command, eventData);
            return ValueTask.FromResult(result);
        }

        public ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Throw(command, eventData);
            return ValueTask.FromResult(result);
        }

        private static void Throw(DbCommand command, CommandExecutedEventData eventData)
        {
            if (Volatile.Read(ref _armed) != 1
                || eventData.Context is not AppTransactionsDbContext
                || !command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal)
                || !command.CommandText.Contains("transactions", StringComparison.Ordinal)
                || Interlocked.Increment(ref _fired) != 1)
            {
                return;
            }

            throw new PostgresException(
                "simulated lost acknowledgement after insert", "ERROR", "ERROR", "40001");
        }
    }

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

    private async Task<Unit> SeedUnitAsync()
    {
        Unit unit = CreateTestUnit();

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        catalog.AddRange(_pendingProperties);
        _pendingProperties.Clear();
        catalog.Add(unit);
        await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);

        return unit;
    }

    [Fact]
    public async Task AHoldWhoseCommitLosesItsAcknowledgement_ReturnsTheHoldItAlreadyCreated()
    {
        // The retry used to mint a second id, so the insert overlapped attempt
        // one's committed range and the exclusion constraint refused it. The
        // caller got 409 for a hold that exists - and that hold carries the
        // same client_key, so it also consumed one of this guest's concurrent
        // slots for its full lifetime, unreachable and uncancellable.
        Unit unit = await SeedUnitAsync();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(140);

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddScoped<AuditableEntitySaveChangesInterceptor, LoseTheAckOnFirstBookingsCommitFor>()));

        using HttpClient client = host.CreateClient();

        LoseTheAckOnFirstBookingsCommitFor.Arm();

        HttpResponseMessage response;

        try
        {
            response = await client.PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            LoseTheAckOnFirstBookingsCommitFor.Disarm();
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(LoseTheAckOnFirstBookingsCommitFor.Fired, "The lost acknowledgement never reached the hold.");

        HoldAvailabilityResponse? hold = await response.Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppBookingsDbContext bookings = assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        // Exactly one, and it is the one the caller was handed. Two rows would
        // mean the retry inserted a second; zero would mean the response names
        // a hold that does not exist.
        List<UnitAvailabilityHold> held = await bookings.UnitAvailabilityHolds.AsNoTracking()
            .Where(h => h.UnitId == unit.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(hold.HoldId, Assert.Single(held).Id);
    }

    [Fact]
    public async Task AHoldRecoveringAtTheCap_IsNotRefusedByItsOwnCommittedHold()
    {
        // The test above runs far below the cap, so it only ever exercised the
        // recovery in the insert's catch - a path that was already reachable.
        //
        // The cap check runs *before* the insert. A retry counts the hold its
        // own previous attempt committed, so a client at the limit is refused
        // with 429 and never reaches that catch at all. With a cap of one, a
        // single lost acknowledgement locks the client out until the hold
        // expires, while the row it cannot see keeps blocking that range.
        Unit unit = await SeedUnitAsync();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(142);

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // App:Holds, not Bookings:HoldCap - HoldCapOptions binds
                    // AppSection("Holds"), and AppSection prefixes "App". The
                    // first version of this key overrode nothing, leaving the
                    // testing default of 100000 in place and the test green
                    // without ever reaching the cap.
                    ["App:Holds:MaxActiveHoldsPerClient"] = "1"
                }));

            builder.ConfigureServices(services =>
                services.AddScoped<AuditableEntitySaveChangesInterceptor, LoseTheAckOnFirstBookingsCommitFor>());
        });

        using HttpClient client = host.CreateClient();

        LoseTheAckOnFirstBookingsCommitFor.Arm();

        HttpResponseMessage response;

        try
        {
            response = await client.PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            LoseTheAckOnFirstBookingsCommitFor.Disarm();
        }

        // 200, not 429. The retry recognises its own committed hold before the
        // cap gets a chance to count it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(LoseTheAckOnFirstBookingsCommitFor.Fired, "The lost acknowledgement never reached the hold.");

        HoldAvailabilityResponse? hold = await response.Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppBookingsDbContext bookings = assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        List<UnitAvailabilityHold> held = await bookings.UnitAvailabilityHolds.AsNoTracking()
            .Where(h => h.UnitId == unit.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(hold.HoldId, Assert.Single(held).Id);
    }

    [Fact]
    public async Task ATransactionWhoseCommitLosesItsAcknowledgement_ReturnsTheTransactionItAlreadyCreated()
    {
        // Same shape, different constraint. The retry re-inserts the same id -
        // Transaction.Create runs once, outside the SaveChanges the strategy
        // retries - so it violates the primary key. The catch matched
        // UniqueViolation broadly and could not tell that from the
        // active-transaction index, so it reported "already in progress": true,
        // and useless, because the guest has no way to learn the id of the
        // transaction they just created.
        Unit unit = await SeedUnitAsync();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(141);

        HttpClient plain = factory.CreateClient();

        HoldAvailabilityResponse? hold = await (await plain.PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken)).Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);

        ConfirmBookingResponse? booking = await (await plain.PostAsJsonAsync("/api/bookings", new ConfirmBookingRequest
        {
            HoldId = hold.HoldId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        }, TestContext.Current.CancellationToken)).Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(booking);

        // The management token buys a session, and the session is what every
        // booking-scoped call carries - see docs/adr/0023. Opened on the plain
        // client so the interceptor below only ever sees the initiation.
        HttpResponseMessage exchange = await plain.PostAsJsonAsync(
            $"/api/bookings/{booking.BookingId}/manage/session",
            new CreateBookingSessionRequest
            {
                BookingId = booking.BookingId,
                ManagementToken = booking.ManagementToken!
            }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        CreateBookingSessionResponse? session = await exchange.Content
            .ReadFromJsonAsync<CreateBookingSessionResponse>(
                TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        string sessionToken = session.SessionToken;

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddScoped<AuditableEntitySaveChangesInterceptor, LoseTheAckOnFirstTransactionsCommit>()));

        using HttpClient client = host.CreateClient();

        LoseTheAckOnFirstTransactionsCommit.Arm();

        HttpResponseMessage response;

        try
        {
            HttpRequestMessage initiate = new HttpRequestMessage(HttpMethod.Post, "/api/transactions")
            {
                Content = JsonContent.Create(new InitiateTransactionRequest { BookingId = booking.BookingId })
            };
            initiate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

            response = await client.SendAsync(initiate, TestContext.Current.CancellationToken);
        }
        finally
        {
            LoseTheAckOnFirstTransactionsCommit.Disarm();
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(LoseTheAckOnFirstTransactionsCommit.Fired, "The lost acknowledgement never reached the transaction.");

        InitiateTransactionResponse? initiated = await response.Content
            .ReadFromJsonAsync<InitiateTransactionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(initiated);

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppTransactionsDbContext transactions =
            assertScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

        List<Transaction> rows = await transactions.Transactions.AsNoTracking()
            .Where(t => t.BookingId == booking.BookingId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(initiated.TransactionId, Assert.Single(rows).Id);
    }
}
