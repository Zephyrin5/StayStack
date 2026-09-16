// Proves each creation handler recognises its own committed insert after a lost acknowledgement
// (FailAfterCommit, targeted at the commit under test). Each fails with its handler's recovery
// disabled, including when a background commit takes the first injected fault.
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Transactions;
using Transactions.Entities;
using Transactions.Features.InitiateTransaction;
using Persistence;
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
    // After the commit that made a hold for this unit durable. Holds are
    // inserted through Dapper, so the commit is recognised by the row it left
    // rather than by the change tracker - and targeted at all because TickerQ's
    // jobs commit on this context too, and one of them taking the injection
    // left this test green with recovery disabled.
    private static CommitFault<AppDbContext> LoseTheAckOnTheHoldFor(Guid unitId) =>
        CommitFaults.FailAfterCommit<AppDbContext>((context, ct) => CommitFaults.CommittedRowExistsAsync(
            context, "SELECT 1 FROM unit_availability_holds WHERE unit_id = @UnitId", "UnitId", unitId, ct));

    // After the commit carrying a payment for this booking - still tracked,
    // since SaveChangesAsync accepts it rather than detaching it.
    private static CommitFault<AppDbContext> LoseTheAckOnThePaymentFor(Guid bookingId) =>
        CommitFaults.FailAfterCommit<AppDbContext>(context =>
            context.ChangeTracker.Entries<Transaction>().Any(e => e.Entity.BookingId == bookingId));

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

    private async Task<Unit> SeedUnitAsync()
    {
        Unit unit = CreateTestUnit();

        using IServiceScope scope = factory.Services.CreateScope();
        CatalogDb catalog = scope.ServiceProvider.GetRequiredService<CatalogDb>();
        catalog.AddRange(_pendingProperties);
        _pendingProperties.Clear();
        catalog.Add(unit);
        await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);

        return unit;
    }

    [Fact]
    public async Task AHoldWhoseCommitLosesItsAcknowledgement_ReturnsTheHoldItAlreadyCreated()
    {
        // A retry minting a second id would overlap attempt one's committed
        // range, and the exclusion constraint would refuse it: 409 for a hold
        // that exists, carrying the same client_key and so consuming one of
        // this guest's concurrent slots for its full lifetime.
        Unit unit = await SeedUnitAsync();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(140);

        CommitFault<AppDbContext> lostAck = LoseTheAckOnTheHoldFor(unit.Id);
        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);

        using HttpClient client = host.CreateClient();

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
            lostAck.Disarm();
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the hold.");

        HoldAvailabilityResponse? hold = await response.Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);

        using IServiceScope assertScope = factory.Services.CreateScope();
        BookingsDb bookings = assertScope.ServiceProvider.GetRequiredService<BookingsDb>();

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

        CommitFault<AppDbContext> lostAck = LoseTheAckOnTheHoldFor(unit.Id);

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // App:Holds, not Bookings:HoldCap - HoldCapOptions binds
                    // AppSection("Holds"), and AppSection prefixes "App". A
                    // wrong key overrides nothing, leaving the testing default
                    // of 100000 in place and the test green without reaching
                    // the cap.
                    ["App:Holds:MaxActiveHoldsPerClient"] = "1"
                }));
        }).WithCommitFault(lostAck);

        using HttpClient client = host.CreateClient();

        HttpResponseMessage response;

        // Its own client network, via X-Forwarded-For. The cap counts by
        // client_key, which the endpoint derives from the remote address - and
        // in this host every request arrives from loopback, so with a cap of 1
        // any hold taken by any other test in the run would exhaust it. This
        // test passed alone and failed in a full run until it stopped sharing.
        //
        // The header is honoured because ForwardedHeadersOptions seeds
        // KnownProxies with ::1, which is exactly where these requests come
        // from - the same default Program.cs's startup guard exists to stop
        // anyone relying on in production.
        HttpRequestMessage hold = new HttpRequestMessage(HttpMethod.Post, "/api/availability/holds")
        {
            Content = JsonContent.Create(new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            })
        };
        hold.Headers.Add("X-Forwarded-For", "203.0.113.47");

        try
        {
            response = await client.SendAsync(hold, TestContext.Current.CancellationToken);
        }
        finally
        {
            lostAck.Disarm();
        }

        // 200, not 429. The retry recognises its own committed hold before the
        // cap gets a chance to count it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the hold.");

        HoldAvailabilityResponse? recovered = await response.Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(recovered);

        using IServiceScope assertScope = factory.Services.CreateScope();
        BookingsDb bookings = assertScope.ServiceProvider.GetRequiredService<BookingsDb>();

        List<UnitAvailabilityHold> held = await bookings.UnitAvailabilityHolds.AsNoTracking()
            .Where(h => h.UnitId == unit.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(recovered.HoldId, Assert.Single(held).Id);
    }

    [Fact]
    public async Task ATransactionWhoseCommitLosesItsAcknowledgement_ReturnsTheTransactionItAlreadyCreated()
    {
        // The first attempt commits and loses its acknowledgement. The retry
        // must answer with the transaction that attempt created: the guest has
        // no other way to learn its id, and "already in progress" - which is
        // what the active-transaction check says about the guest's own row -
        // is true and useless.
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

        CommitFault<AppDbContext> lostAck = LoseTheAckOnThePaymentFor(booking.BookingId);
        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);

        using HttpClient client = host.CreateClient();

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
            lostAck.Disarm();
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the transaction.");

        InitiateTransactionResponse? initiated = await response.Content
            .ReadFromJsonAsync<InitiateTransactionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(initiated);

        using IServiceScope assertScope = factory.Services.CreateScope();
        TransactionsDb transactions =
            assertScope.ServiceProvider.GetRequiredService<TransactionsDb>();

        List<Transaction> rows = await transactions.Transactions.AsNoTracking()
            .Where(t => t.BookingId == booking.BookingId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(initiated.TransactionId, Assert.Single(rows).Id);
    }
}
