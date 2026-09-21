// Three tests proving different things - see the header. With BookingPaymentLock removed, the
// pre-lock cancellation test still passes (it proves the re-read); the in-lock cancellation and
// expiry tests fail (they prove the lock).
using Bookings;
using Bookings.Contracts;
using BuildingBlocks.Persistence;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.CreateBookingSession;
using Bookings.Features.HoldAvailability;
using Bookings.Jobs;
using Catalog;
using Catalog.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Transactions;
using Transactions.Features.InitiateTransaction;
namespace IntegrationTests.Features.Transactions;

// Initiation checks the booking is payable and inserts the transaction. Without
// BookingPaymentLock and a re-read under it, a cancellation committing in
// between leaves a pending payment against a cancelled booking.
//
// On its own that is untidy - no money moves. What makes it matter is what it
// manufactures: if the cancellation also moved an earlier payment to
// RefundPending, the active-transaction check matches nothing and the insert
// succeeds, leaving one booking with a RefundPending *and* a Succeeded
// transaction, a pair every booking-wide read has to handle.
//
// Which test proves what - the pairing looks symmetrical and is not:
//
// - ACancellationCommittingBeforeInitiationTakesTheLock_IsSeenByTheReRead pauses
//   initiation BEFORE BookingPaymentLock. It proves the re-read under the lock
//   catches a cancellation that finished first. It passes with the lock removed.
// - ACancellationDuringInitiationsLockedSection_WaitsForIt pauses INSIDE the lock
//   and observes the cancellation waiting on it in pg_locks. It proves the lock
//   excludes cancellation. It fails with the lock removed.
// - AnExpiryDuringInitiationsLockedSection_StepsOverTheBooking pauses inside the
//   lock and proves expiry steps over it. It fails with the lock removed.
[Collection(TransactionsCollection.Name)]
public class PaymentInitiationRaceTests(TransactionsFixture factory)
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

    // Pauses initiation between a booking check and its insert - the window the
    // whole defect lives in.
    //
    // Two checks, two windows. The access check runs before BookingPaymentLock
    // is taken; the re-read runs after it, inside the section the lock
    // protects. Pausing at the first proves the re-read catches what happened
    // before the lock. Only pausing at the second proves the lock excludes
    // anything at all.
    private sealed class PauseAfterTheBookingCheck(
        IBookingLookup inner, TaskCompletionSource gate, TaskCompletionSource reached, bool insideTheLock)
        : IBookingLookup
    {
        private int _fired;

        private async Task PauseOnceAsync()
        {
            if (Interlocked.Increment(ref _fired) == 1)
            {
                reached.TrySetResult();
                await gate.Task;
            }
        }

        public async Task<BookingAccessResult?> VerifyBookingAccessAsync(
            Guid bookingId, Guid? customerId, CancellationToken cancellationToken)
        {
            BookingAccessResult? result = await inner.VerifyBookingAccessAsync(bookingId, customerId, cancellationToken);

            if (!insideTheLock)
            {
                await PauseOnceAsync();
            }

            return result;
        }

        public async Task<BookingAccessResult?> GetBookingDetailsAsync(Guid bookingId, CancellationToken cancellationToken)
        {
            BookingAccessResult? result = await inner.GetBookingDetailsAsync(bookingId, cancellationToken);

            if (insideTheLock)
            {
                await PauseOnceAsync();
            }

            return result;
        }

        public Task<BookingSummary?> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
            inner.GetBookingAsync(bookingId, cancellationToken);

        public Task<IReadOnlyList<BookingAccessResult>> GetConfirmedBookingsForCustomerAsync(
            Guid customerId, DateOnly checkOutFrom, DateOnly checkOutTo, CancellationToken cancellationToken) =>
            inner.GetConfirmedBookingsForCustomerAsync(customerId, checkOutFrom, checkOutTo, cancellationToken);

        public Task<RefundObligationSnapshot?> GetRefundObligationAsync(
            Guid bookingId, CancellationToken cancellationToken) =>
            inner.GetRefundObligationAsync(bookingId, cancellationToken);

        public Task MarkRefundObligationResolvedAsync(
            Guid bookingId, DateTimeOffset resolvedAt, RefundObligationOutcome outcome, CancellationToken cancellationToken) =>
            inner.MarkRefundObligationResolvedAsync(bookingId, resolvedAt, outcome, cancellationToken);
    }

    [Fact]
    public async Task ACancellationCommittingBeforeInitiationTakesTheLock_IsSeenByTheReRead()
    {
        Unit unit = CreateTestUnit();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            CatalogDb catalog = scope.ServiceProvider.GetRequiredService<CatalogDb>();
            catalog.AddRange(_pendingProperties);
            _pendingProperties.Clear();
            catalog.Add(unit);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        HttpClient plain = factory.CreateClient();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(180);

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
        Assert.NotNull(booking?.ManagementToken);

        string session = await OpenSessionAsync(plain, booking.BookingId, booking.ManagementToken);

        TaskCompletionSource gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IBookingLookup));
                services.Remove(original);
                services.AddScoped<IBookingLookup>(sp => new PauseAfterTheBookingCheck(
                    (IBookingLookup)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), gate, reached,
                    insideTheLock: false));
            }));

        using HttpClient paused = host.CreateClient();

        // Act - initiation has seen a payable booking and has not inserted.
        HttpRequestMessage initiate = new HttpRequestMessage(HttpMethod.Post, "/api/transactions")
        {
            Content = JsonContent.Create(new InitiateTransactionRequest { BookingId = booking.BookingId })
        };
        initiate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);

        Task<HttpResponseMessage> initiation = paused.SendAsync(initiate, TestContext.Current.CancellationToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The cancellation, start to finish, inside that window.
        HttpRequestMessage cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{booking.BookingId}/cancel")
        {
            Content = JsonContent.Create(new { bookingId = booking.BookingId, guestEmail = "jane@example.com" })
        };
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);

        Assert.Equal(HttpStatusCode.OK, (await plain.SendAsync(cancel, TestContext.Current.CancellationToken)).StatusCode);

        gate.SetResult();

        HttpResponseMessage response = await initiation;

        // Assert - refused, and nothing left behind. A pending payment against
        // a cancelled booking is the row that goes on to break refund
        // resolution for this booking permanently.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using IServiceScope assertScope = factory.Services.CreateScope();

        Assert.Empty(await assertScope.ServiceProvider.GetRequiredService<TransactionsDb>()
            .Transactions.AsNoTracking()
            .Where(t => t.BookingId == booking.BookingId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnExpiryDuringInitiationsLockedSection_StepsOverTheBooking()
    {
        // Expiry is the other path that cancels a booking. Initiation never
        // touches the booking row - it reads the booking through IBookingLookup
        // and holds BookingPaymentLock - so an expiry holding only the row lock
        // could not see it: initiation re-reads Pending, expiry cancels and
        // releases the unit, and initiation commits a payment against a
        // cancelled booking.
        Unit unit = CreateTestUnit();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            CatalogDb catalog = scope.ServiceProvider.GetRequiredService<CatalogDb>();
            catalog.AddRange(_pendingProperties);
            _pendingProperties.Clear();
            catalog.Add(unit);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        HttpClient plain = factory.CreateClient();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(183);

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
        Assert.NotNull(booking?.ManagementToken);

        string session = await OpenSessionAsync(plain, booking.BookingId, booking.ManagementToken);

        // Overdue, so the expiry below has something to do. Moved on this row
        // alone rather than by advancing a clock, which would expire every
        // other test's pending booking in the shared database too.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BookingsDb>().Bookings
                .Where(b => b.Id == booking.BookingId)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(b => b.PaymentDueAt, DateTimeOffset.UtcNow.AddMinutes(-1)),
                    TestContext.Current.CancellationToken);
        }

        TaskCompletionSource gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IBookingLookup));
                services.Remove(original);
                services.AddScoped<IBookingLookup>(sp => new PauseAfterTheBookingCheck(
                    (IBookingLookup)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), gate, reached,
                    insideTheLock: true));
            }));

        using HttpClient paused = host.CreateClient();

        HttpRequestMessage initiate = new HttpRequestMessage(HttpMethod.Post, "/api/transactions")
        {
            Content = JsonContent.Create(new InitiateTransactionRequest { BookingId = booking.BookingId })
        };
        initiate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);

        // Act - initiation holds the payment lock and has re-read a payable
        // booking. Expiry runs to completion inside that window.
        Task<HttpResponseMessage> initiation = paused.SendAsync(initiate, TestContext.Current.CancellationToken);

        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            using IServiceScope jobScope = factory.Services.CreateScope();

            await new ExpireUnpaidBookingsJob(
                    jobScope.ServiceProvider.GetRequiredService<BookingsDb>(),
                    jobScope.ServiceProvider.GetRequiredService<BuildingBlocks.Persistence.ITransactionRunner>(),
                    jobScope.ServiceProvider.GetRequiredService<IHoldConfirmation>(),
                    jobScope.ServiceProvider.GetRequiredService<global::Promotions.Contracts.IPromotionRedemption>(),
                    jobScope.ServiceProvider.GetRequiredService<IPaymentReversal>(),
                    TimeProvider.System,
                    NullLogger<ExpireUnpaidBookingsJob>.Instance)
                .ExpireAsync(null!, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            gate.TrySetResult();
        }

        HttpResponseMessage response = await initiation;

        // Assert - the sweep stepped over a booking whose payment lock was
        // held, exactly as it steps over a locked row, and initiation's payment
        // is against a booking that is still there to be paid for. Before, the
        // booking came back Cancelled with its unit released and a pending
        // payment beside it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope assertScope = factory.Services.CreateScope();

        Booking current = await assertScope.ServiceProvider.GetRequiredService<BookingsDb>()
            .Bookings.AsNoTracking()
            .SingleAsync(b => b.Id == booking.BookingId, TestContext.Current.CancellationToken);

        Assert.Equal(BookingStatus.Pending, current.BookingStatus);

        Assert.Single(await assertScope.ServiceProvider.GetRequiredService<TransactionsDb>()
            .Transactions.AsNoTracking()
            .Where(t => t.BookingId == booking.BookingId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACancellationDuringInitiationsLockedSection_WaitsForIt()
    {
        // The locked window, which the test above never enters. Initiation holds
        // BookingPaymentLock and has re-read a payable booking; a cancellation
        // arriving now must wait for it rather than cancel underneath it.
        //
        // Observed, not timed: the cancellation takes the lock with a blocking
        // pg_advisory_xact_lock, so while it waits Postgres lists an ungranted
        // advisory lock on this booking's key. With the lock removed there is
        // never a waiter to see, and the cancellation simply finishes.
        (Guid bookingId, string session) = await CheckOutAsync(daysUntilCheckIn: 186);

        TaskCompletionSource gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IBookingLookup));
                services.Remove(original);
                services.AddScoped<IBookingLookup>(sp => new PauseAfterTheBookingCheck(
                    (IBookingLookup)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), gate, reached,
                    insideTheLock: true));
            }));

        HttpRequestMessage initiate = new HttpRequestMessage(HttpMethod.Post, "/api/transactions")
        {
            Content = JsonContent.Create(new InitiateTransactionRequest { BookingId = bookingId })
        };
        initiate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);

        Task<HttpResponseMessage> initiation = host.CreateClient().SendAsync(initiate, TestContext.Current.CancellationToken);
        Task<HttpResponseMessage>? cancellation = null;

        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            HttpRequestMessage cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{bookingId}/cancel")
            {
                Content = JsonContent.Create(new { bookingId, guestEmail = "jane@example.com" })
            };
            cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);

            // Act - the cancellation, while initiation holds the lock.
            cancellation = factory.CreateClient().SendAsync(cancel, TestContext.Current.CancellationToken);

            // Assert - it is waiting on this booking's payment lock, and has not
            // finished.
            Assert.True(await CancellationIsWaitingOnThePaymentLockAsync(bookingId, cancellation),
                "The cancellation never waited on BookingPaymentLock while initiation held it.");
            Assert.False(cancellation.IsCompleted);
        }
        finally
        {
            gate.TrySetResult();
        }

        // Released: initiation commits against the booking it re-read, then the
        // cancellation proceeds - in that order, which is the whole guarantee.
        Assert.Equal(HttpStatusCode.OK, (await initiation).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cancellation).StatusCode);

        using IServiceScope assertScope = factory.Services.CreateScope();

        Assert.Equal(BookingStatus.Cancelled, (await assertScope.ServiceProvider.GetRequiredService<BookingsDb>()
            .Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken)).BookingStatus);

        Assert.Single(await assertScope.ServiceProvider.GetRequiredService<TransactionsDb>()
            .Transactions.AsNoTracking()
            .Where(t => t.BookingId == bookingId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    // Polls pg_locks for an ungranted advisory lock on this booking's payment key,
    // giving up if the cancellation finishes first or ten seconds pass. A bigint
    // advisory key is reported split across classid (high 32 bits) and objid (low
    // 32 bits), with objsubid 1.
    private async Task<bool> CancellationIsWaitingOnThePaymentLockAsync(Guid bookingId, Task cancellation)
    {
        long key = BookingPaymentLock.KeyFor(bookingId);
        long high = (long)((ulong)key >> 32);
        long low = key & 0xFFFFFFFFL;

        using IServiceScope scope = factory.Services.CreateScope();
        BookingsDb db = scope.ServiceProvider.GetRequiredService<BookingsDb>();

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline && !cancellation.IsCompleted)
        {
            bool waiting = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory' AND NOT granted AND objsubid = 1
                      AND classid::bigint = {high} AND objid::bigint = {low}) AS "Value"
                """).SingleAsync(TestContext.Current.CancellationToken);

            if (waiting)
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private async Task<(Guid BookingId, string Session)> CheckOutAsync(int daysUntilCheckIn)
    {
        Unit unit = CreateTestUnit();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            CatalogDb catalog = scope.ServiceProvider.GetRequiredService<CatalogDb>();
            catalog.AddRange(_pendingProperties);
            _pendingProperties.Clear();
            catalog.Add(unit);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        HttpClient plain = factory.CreateClient();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(daysUntilCheckIn);

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
        Assert.NotNull(booking?.ManagementToken);

        return (booking.BookingId, await OpenSessionAsync(plain, booking.BookingId, booking.ManagementToken));
    }

    private static async Task<string> OpenSessionAsync(HttpClient client, Guid bookingId, string managementToken)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/bookings/{bookingId}/manage/session",
            new CreateBookingSessionRequest { BookingId = bookingId, ManagementToken = managementToken },
            TestContext.Current.CancellationToken);

        CreateBookingSessionResponse? session = await response.Content
            .ReadFromJsonAsync<CreateBookingSessionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        return session.SessionToken;
    }
}
