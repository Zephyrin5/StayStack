// Proves each cross-module step commits or rolls back with its half: test 1 fails inside the
// confirmation's transaction before its commit, test 2 is pre-commit, test 3 is post-commit
// (BookingPaymentConfirmation commits its own transaction), test 4 forces overlap by loading both
// sides first. Not verified by breaking the mechanisms; lost acknowledgements are ConfirmRetryTests'.
using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Outbox;
using Bookings.Features.ConfirmBooking;
using Bookings.Jobs;
using Catalog;
using Catalog.Entities;
using Bookings.Features.HoldAvailability;
using Identity.Entities;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NpgsqlTypes;
using Promotions.Contracts;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Transactions;
using Transactions.Entities;
using System.Net;
using Transactions.Features.InitiateTransaction;
using Transactions.Features.MarkTransactionFailed;
using Transactions.Outbox;
using Transactions.Features.MarkTransactionSucceeded;
namespace IntegrationTests.Features.Bookings;

// A specification, not a regression suite: these describe what the booking
// lifecycle must guarantee.
//
// Every one of them is the same shape - a write commits, and then the caller
// never learns that it did. That is not an exotic failure. It is what a
// dropped connection, a killed process or a timeout on the acknowledgement
// looks like from the outside, and it is the case that distinguishes a
// system that recovers from one that strands inventory or money. The
// compensating machinery is tested elsewhere for the paths where the *call*
// fails; these cover the path where the call succeeds and the answer is lost.
//
// Do not weaken an assertion here to match current behaviour. If one of
// these has to change, the reason should be that the guarantee itself was
// wrong, argued on its own terms.
[Collection("Integration Tests")]
public class CommitAmbiguitySpecTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    // ---- seeding helpers -------------------------------------------------

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
            100);
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

    private async Task<Guid> HoldUnitAsync(HttpClient client, Guid unitId, int daysUntilCheckIn = 7)
    {
        DateOnly checkIn = CatalogSeeding.Today().AddDays(daysUntilCheckIn);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
        {
            UnitId = unitId,
            CheckIn = checkIn,
            CheckOut = checkIn.AddDays(3),
            GuestCount = 2
        }, TestContext.Current.CancellationToken);

        HoldAvailabilityResponse? hold =
            await response.Content.ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);
        return hold.HoldId;
    }

    private async Task<string> SeedSignedInCustomerAsync()
    {
        string email = $"{Guid.NewGuid():N}@example.com";
        const string password = "P@ssw0rd!spec";

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
            Assert.True((await userManager.CreateAsync(user, password)).Succeeded);
        }

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);
        SignInResponse? result =
            await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string uri, string accessToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    // ---- 1: the hold transition is written, then its transaction fails -----

    // Delegates to the real implementation and then throws. HoldConfirmation
    // joins the caller's transaction, so the transition is written but not
    // committed and the throw rolls it back: this exercises a failure after the
    // write and before the commit. The lost acknowledgement of the confirmation
    // commit is ConfirmRetryTests', on CommitFaults.FailAfterCommit.
    private sealed class ConfirmHoldThenFailTheTransaction(IHoldConfirmation inner) : IHoldConfirmation
    {
        public async Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken)
        {
            await inner.ConfirmHoldAsync(holdId, cancellationToken);
            throw new InvalidOperationException("Failure after the hold transition was written, before the commit.");
        }

        public Task<bool> MarkHoldPaidAsync(Guid holdId, CancellationToken cancellationToken) =>
            inner.MarkHoldPaidAsync(holdId, cancellationToken);

        public Task ReleaseHoldAsync(Guid holdId, CancellationToken cancellationToken) =>
            inner.ReleaseHoldAsync(holdId, cancellationToken);
    }

    [Fact]
    public async Task AHoldTransitionWhoseTransactionFailsBeforeCommitting_LeavesNoHoldMovedWithoutABooking()
    {
        // The booking and the transition must agree. A hold left
        // pending_payment with no booking is invisible to every recovery path:
        // nothing releases it and nothing knows it exists. Asserting the two
        // agree is the whole specification, and it holds regardless of where in
        // the sequence the failure lands.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        HttpClient client = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IHoldConfirmation));
                services.Remove(original);
                services.AddScoped<IHoldConfirmation>(sp => new ConfirmHoldThenFailTheTransaction(
                    (IHoldConfirmation)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)));
            })).CreateClient();

        Guid holdId = await HoldUnitAsync(client, unit.Id);

        // Act - the confirmation fails from the caller's point of view.
        await client.PostAsJsonAsync("/api/bookings", new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        }, TestContext.Current.CancellationToken);

        // Assert - whatever the outcome, the system must still be able to
        // reach that hold. Either the transition did not stick, or a booking
        // points at it.
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext availability = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        AppBookingsDbContext bookings = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        UnitAvailabilityHold hold = await availability.UnitAvailabilityHolds.AsNoTracking()
            .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken);

        bool holdMoved = hold.Status == "pending_payment";

        bool booked =
            await bookings.Bookings.AsNoTracking().AnyAsync(b => b.HoldId == holdId, TestContext.Current.CancellationToken);

        // Biconditional, not implication. "Moved implies booked" alone would
        // also be satisfied by a booking committed over a hold still 'held' -
        // inventory sold that anyone else can hold again.
        Assert.True(
            holdMoved == booked,
            holdMoved
                ? "A hold left in pending_payment must have a booking pointing at it. " +
                  "Without one, the unit is held for a checkout nobody can find and nothing will release."
                : "A hold that was rolled back to 'held' must have no booking. " +
                  "A booking over an unclaimed hold is inventory anyone can hold again.");
    }

    // ---- 2: inventory released, then the expiry rolls back ---------------

    // Releases the hold for real and then throws, so the release has happened
    // by the time the expiry fails. Injected here rather than through the
    // promotion reversal, which is what this test used before: the reversal is
    // now an outbox row dispatched after the commit, so failing it proves
    // nothing about what the transaction did. The release is the write that
    // has to be inside.
    private sealed class ReleaseHoldThenFail(IHoldConfirmation inner) : IHoldConfirmation
    {
        public Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken) =>
            inner.ConfirmHoldAsync(holdId, cancellationToken);

        public Task<bool> MarkHoldPaidAsync(Guid holdId, CancellationToken cancellationToken) =>
            inner.MarkHoldPaidAsync(holdId, cancellationToken);

        public async Task ReleaseHoldAsync(Guid holdId, CancellationToken cancellationToken)
        {
            await inner.ReleaseHoldAsync(holdId, cancellationToken);
            throw new InvalidOperationException("Connection lost after the hold was released.");
        }
    }

    [Fact]
    public async Task AnExpiryThatFailsAfterReleasingTheHold_DoesNotLeaveTheBookingHoldingNothing()
    {
        // The release and the cancellation share one DbContext and one
        // transaction, so the failure injected below must take the release with
        // it. Were the release to commit on its own, the booking would stay
        // Pending while its inventory went back on sale, and the next sweep
        // would release a hold somebody else might own.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = CatalogSeeding.Today().AddDays(20);
        Guid holdId = Guid.CreateVersion7();
        Guid bookingId = Guid.CreateVersion7();

        using (IServiceScope seedScope = factory.Services.CreateScope())
        {
            AppBookingsDbContext availability = seedScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            availability.UnitAvailabilityHolds.Add(new UnitAvailabilityHold
            {
                Id = holdId,
                UnitId = unit.Id,
                StayRange = new NpgsqlRange<DateOnly>(checkIn, true, checkIn.AddDays(2), false),
                Status = "pending_payment",
                GuestCount = 2,
                CreatedAt = DateTimeOffset.UtcNow,
                TotalPrice = Money.Of(200m, Currency.KWD),
                Subtotal = 200m
            });
            await availability.SaveChangesAsync(TestContext.Current.CancellationToken);

            AppBookingsDbContext bookingsDb = seedScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            bookingsDb.Bookings.Add(Booking.Create(
                bookingId, unit.Id, holdId, null, "Jane Guest", "jane@example.com", null,
                checkIn, checkIn.AddDays(2), 2,
                Money.Of(200m, Currency.KWD), Money.Of(200m, Currency.KWD),
                CancellationPolicy.CreateDefault(), "Asia/Kuwait",
                DateTimeOffset.UtcNow.AddMinutes(-1)));
            await bookingsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using IServiceScope scope = factory.Services.CreateScope();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(DateTimeOffset.UtcNow);

        ExpireUnpaidBookingsJob job = new ExpireUnpaidBookingsJob(
            scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>(),
            new ReleaseHoldThenFail(scope.ServiceProvider.GetRequiredService<IHoldConfirmation>()),
            scope.ServiceProvider.GetRequiredService<global::Transactions.Contracts.ITransactionLookup>(),
            scope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>(),
            timeProvider,
            NullLogger<ExpireUnpaidBookingsJob>.Instance);

        // Act - the job swallows the per-row failure and moves on.
        await job.ExpireAsync(null!, TestContext.Current.CancellationToken);

        // Assert
        using IServiceScope assertScope = factory.Services.CreateScope();
        string holdStatus = (await assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>()
            .UnitAvailabilityHolds.AsNoTracking()
            .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken)).Status;
        BookingStatus bookingStatus = (await assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>()
            .Bookings.AsNoTracking()
            .SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken)).BookingStatus;

        Assert.False(
            holdStatus == "held" && bookingStatus == BookingStatus.Pending,
            "The hold was released while its booking is still Pending: the guest holds a live booking whose " +
            "dates are back on sale, and the next sweep will try to release a hold someone else may now own.");
    }

    // ---- 3: the booking confirms, then the acknowledgement is lost -------

    private sealed class ConfirmPaymentThenLoseTheAnswer(IBookingPaymentConfirmation inner) : IBookingPaymentConfirmation
    {
        public async Task<bool> ConfirmPaymentAsync(Guid bookingId, CancellationToken cancellationToken)
        {
            await inner.ConfirmPaymentAsync(bookingId, cancellationToken);
            throw new InvalidOperationException("Connection lost after the booking was confirmed.");
        }
    }

    [Fact]
    public async Task ABookingConfirmedByAPaymentWhoseAnswerWasLost_IsNotRefunded()
    {
        // The dispatcher retries, exhausts its attempts, dead-letters, and
        // OnDeadLetteredAsync then refunds any still-Succeeded transaction -
        // without asking whether the booking it was paying for actually
        // confirmed. It did: every one of those attempts confirmed it again.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string customerToken = await SeedSignedInCustomerAsync();

        // The derived host is kept, not just its client: the relay loop below
        // has to run inside the same host, or it resolves the real
        // confirmation, succeeds on the first pass, and the message never
        // accumulates the attempts this test depends on.
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IBookingPaymentConfirmation));
                services.Remove(original);
                services.AddScoped<IBookingPaymentConfirmation>(sp => new ConfirmPaymentThenLoseTheAnswer(
                    (IBookingPaymentConfirmation)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)));
            }));
        HttpClient client = host.CreateClient();

        Guid holdId = await HoldUnitAsync(client, unit.Id);

        using HttpRequestMessage confirmRequest = Authorized(HttpMethod.Post, "/api/bookings", customerToken);
        confirmRequest.Content = JsonContent.Create(new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        });
        HttpResponseMessage confirmResponse = await client.SendAsync(confirmRequest, TestContext.Current.CancellationToken);
        ConfirmBookingResponse? booking =
            await confirmResponse.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(booking);

        Guid transactionId = await InitiateTransactionAsync(client, booking.BookingId, customerToken);

        string adminToken = await IntegrationTestAdmin.SignInAsync(client, TestContext.Current.CancellationToken);
        using HttpRequestMessage succeedRequest = Authorized(HttpMethod.Post, $"/api/transactions/{transactionId}/succeed", adminToken);
        await client.SendAsync(succeedRequest, TestContext.Current.CancellationToken);

        // Drive the message to dead-letter. Rather than replaying the
        // backoff, the row is advanced to its final attempt and dispatched
        // once: what this test is about is what happens *at* dead-letter,
        // not how many retries precede it, and replaying ten real backoff
        // windows would only make the test slow and timing-dependent.
        using (IServiceScope arrangeScope = host.Services.CreateScope())
        {
            AppTransactionsDbContext arrangeDb = arrangeScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();
            await arrangeDb.Database.ExecuteSqlRawAsync(
                $"""
                UPDATE "transactions_outbox_messages"
                -- An hour in the past, not now(): the dispatcher compares
                -- against its own TimeProvider, and "exactly now" by
                -- Postgres' clock is not reliably <= "now" by the app's.
                SET attempts = 9, next_attempt_at = now() - interval '1 hour'
                WHERE type = '{ConfirmBookingPaymentOutboxMessage.TypeName}'
                  AND processed_at IS NULL AND dead_lettered_at IS NULL
                """,
                TestContext.Current.CancellationToken);
        }

        using (IServiceScope relayScope = host.Services.CreateScope())
        {
            await relayScope.ServiceProvider.GetRequiredService<TransactionsOutboxDispatcher>()
                .DispatchPendingAsync(50, TestContext.Current.CancellationToken);
        }

        // Assert - first that the scenario actually happened. If the message
        // never dead-lettered, the invariant below would hold vacuously and
        // this test would be green while proving nothing.
        using IServiceScope assertScope = factory.Services.CreateScope();

        AppTransactionsDbContext transactionsDb = assertScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();
        var confirmationMessages = await transactionsDb.TransactionsOutboxMessages.AsNoTracking()
            .Where(m => m.Type == ConfirmBookingPaymentOutboxMessage.TypeName)
            .Select(m => new { m.Attempts, m.ProcessedAt, m.DeadLetteredAt })
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(
            confirmationMessages.Any(m => m.Attempts >= 10),
            "The confirmation message never exhausted its attempts, so this test never reached the case it describes. Rows: " +
            string.Join("; ", confirmationMessages.Select(m => $"attempts={m.Attempts} processed={m.ProcessedAt} deadLettered={m.DeadLetteredAt}")));

        Booking persisted = await assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>()
            .Bookings.AsNoTracking().SingleAsync(b => b.Id == booking.BookingId, TestContext.Current.CancellationToken);
        Transaction transaction = await transactionsDb.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);

        Assert.False(
            persisted.BookingStatus == BookingStatus.Confirmed
            && transaction.TransactionStatus == TransactionStatus.RefundPending,
            "The booking is Confirmed and its payment is queued for refund: the guest keeps a stay they are " +
            "about to be refunded for. A dead-lettered confirmation must check whether the booking committed.");
    }

    private static async Task<Guid> InitiateTransactionAsync(HttpClient client, Guid bookingId, string customerToken)
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Post, "/api/transactions", customerToken);
        request.Content = JsonContent.Create(new InitiateTransactionRequest { BookingId = bookingId });

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Asserted, not assumed: a transaction that was never created would
        // make both transitions below fail for a reason that has nothing to
        // do with what these tests are about.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        InitiateTransactionResponse? result =
            await response.Content.ReadFromJsonAsync<InitiateTransactionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.TransactionId);
        return result.TransactionId;
    }

    // ---- 4: two terminal transitions race -------------------------------

    [Fact]
    public async Task TwoTerminalTransactionTransitions_CannotBothWin()
    {
        // MarkSucceeded and MarkFailed both load the transaction, check it is
        // Pending, mutate and save. Nothing arbitrates between them: no row
        // lock, no conditional update, no concurrency token. Two callers that
        // read before either writes both pass their own guard.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string customerToken = await SeedSignedInCustomerAsync();

        Guid holdId = await HoldUnitAsync(_client, unit.Id);

        using HttpRequestMessage confirmRequest = Authorized(HttpMethod.Post, "/api/bookings", customerToken);
        confirmRequest.Content = JsonContent.Create(new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        });
        HttpResponseMessage confirmResponse = await _client.SendAsync(confirmRequest, TestContext.Current.CancellationToken);
        ConfirmBookingResponse? booking =
            await confirmResponse.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(booking);

        Guid transactionId = await InitiateTransactionAsync(_client, booking.BookingId, customerToken);

        // Act - both readers observe Pending before either writes. Racing two
        // handler invocations with Task.WhenAll would not guarantee that:
        // the first can finish entirely before the second reads, and the
        // second's own guard would then reject it, so the test would pass
        // without the system arbitrating anything. Loading both first makes
        // the interleaving the point rather than an accident.
        using IServiceScope succeedScope = factory.Services.CreateScope();
        using IServiceScope failScope = factory.Services.CreateScope();

        AppTransactionsDbContext succeedDb = succeedScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();
        AppTransactionsDbContext failDb = failScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

        Transaction forSuccess = await succeedDb.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
        Transaction forFailure = await failDb.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);

        Assert.Equal(TransactionStatus.Pending, forSuccess.TransactionStatus);
        Assert.Equal(TransactionStatus.Pending, forFailure.TransactionStatus);

        async Task<bool> TryCommitAsync(AppTransactionsDbContext context, Action transition)
        {
            try
            {
                transition();
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
        }

        bool[] outcomes =
        [
            await TryCommitAsync(succeedDb, () => forSuccess.MarkSucceeded(DateTimeOffset.UtcNow)),
            await TryCommitAsync(failDb, () => forFailure.MarkFailed("declined"))
        ];

        // Assert - exactly one terminal transition may be accepted.
        Assert.Equal(1, outcomes.Count(won => won));
    }
}
