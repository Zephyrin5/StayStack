// Proves each cross-module step commits or rolls back with its half: test 1 fails inside the
// confirmation's scope before its commit, test 2 is pre-commit, test 3 loses the payment's commit
// acknowledgement (FailAfterCommit), test 4 forces overlap by loading both sides first. Tests 1 and
// 3 were verified by breaking the mechanism (committing a failed scope; removing the payment's
// recovery branch); 2 and 4 were not. The confirmation's lost acknowledgement is ConfirmRetryTests'.
using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
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
using Transactions.Features.MarkTransactionSucceeded;
using Persistence;
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
        CatalogDb context = scope.ServiceProvider.GetRequiredService<CatalogDb>();
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
        BookingsDb availability = scope.ServiceProvider.GetRequiredService<BookingsDb>();
        BookingsDb bookings = scope.ServiceProvider.GetRequiredService<BookingsDb>();

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
    // by the time the expiry fails. The release is the write that has to be
    // inside the expiry's scope.
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
            BookingsDb availability = seedScope.ServiceProvider.GetRequiredService<BookingsDb>();
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

            BookingsDb bookingsDb = seedScope.ServiceProvider.GetRequiredService<BookingsDb>();
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
            scope.ServiceProvider.GetRequiredService<BookingsDb>(),
            scope.ServiceProvider.GetRequiredService<BuildingBlocks.Persistence.ITransactionRunner>(),
            new ReleaseHoldThenFail(scope.ServiceProvider.GetRequiredService<IHoldConfirmation>()),
            scope.ServiceProvider.GetRequiredService<global::Promotions.Contracts.IPromotionRedemption>(),
            scope.ServiceProvider.GetRequiredService<IPaymentReversal>(),
            timeProvider,
            NullLogger<ExpireUnpaidBookingsJob>.Instance);

        // Act - the job swallows the per-row failure and moves on.
        await job.ExpireAsync(null!, TestContext.Current.CancellationToken);

        // Assert
        using IServiceScope assertScope = factory.Services.CreateScope();
        string holdStatus = (await assertScope.ServiceProvider.GetRequiredService<BookingsDb>()
            .UnitAvailabilityHolds.AsNoTracking()
            .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken)).Status;
        BookingStatus bookingStatus = (await assertScope.ServiceProvider.GetRequiredService<BookingsDb>()
            .Bookings.AsNoTracking()
            .SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken)).BookingStatus;

        Assert.False(
            holdStatus == "held" && bookingStatus == BookingStatus.Pending,
            "The hold was released while its booking is still Pending: the guest holds a live booking whose " +
            "dates are back on sale, and the next sweep will try to release a hold someone else may now own.");
    }

    // ---- 3: the payment confirms the booking, then the acknowledgement is lost

    [Fact]
    public async Task ABookingConfirmedByAPaymentWhoseAnswerWasLost_IsNotRefunded()
    {
        // The payment's commit confirms the booking and marks the transaction
        // Succeeded, and the caller never hears. The execution strategy runs the
        // work again against a transaction already Succeeded. Refunding it, or
        // answering 409, would treat the payment's own committed success as
        // something else's.
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

        // After the commit that marks this transaction Succeeded.
        CommitFault<AppDbContext> lostAck = CommitFaults.FailAfterCommit<AppDbContext>(context =>
            context.ChangeTracker.Entries<Transaction>()
                .Any(e => e.Entity.Id == transactionId && e.Entity.TransactionStatus == TransactionStatus.Succeeded));

        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);
        HttpClient client = host.CreateClient();

        string adminToken = await IntegrationTestAdmin.SignInAsync(client, TestContext.Current.CancellationToken);
        using HttpRequestMessage succeedRequest = Authorized(HttpMethod.Post, $"/api/transactions/{transactionId}/succeed", adminToken);
        HttpResponseMessage succeeded = await client.SendAsync(succeedRequest, TestContext.Current.CancellationToken);

        // First that the scenario happened: without it the assertions below hold
        // vacuously.
        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the payment's commit.");
        Assert.Equal(HttpStatusCode.OK, succeeded.StatusCode);

        using IServiceScope assertScope = factory.Services.CreateScope();

        Booking persisted = await assertScope.ServiceProvider.GetRequiredService<BookingsDb>()
            .Bookings.AsNoTracking().SingleAsync(b => b.Id == booking.BookingId, TestContext.Current.CancellationToken);
        Transaction transaction = await assertScope.ServiceProvider.GetRequiredService<TransactionsDb>()
            .Transactions.AsNoTracking().SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);

        Assert.Equal(BookingStatus.Confirmed, persisted.BookingStatus);
        Assert.Equal(TransactionStatus.Succeeded, transaction.TransactionStatus);
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

        TransactionsDb succeedDb = succeedScope.ServiceProvider.GetRequiredService<TransactionsDb>();
        TransactionsDb failDb = failScope.ServiceProvider.GetRequiredService<TransactionsDb>();

        Transaction forSuccess = await succeedDb.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
        Transaction forFailure = await failDb.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);

        Assert.Equal(TransactionStatus.Pending, forSuccess.TransactionStatus);
        Assert.Equal(TransactionStatus.Pending, forFailure.TransactionStatus);

        async Task<bool> TryCommitAsync(TransactionsDb context, Action transition)
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
