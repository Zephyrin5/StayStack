// AUDIT 2026-09-14: Probed with BookingPaymentLock removed: the cancellation test still PASSES - it pauses before the lock and proves only the re-read; the expiry test fails - it pauses inside the lock and is the one that proves it. Fresh-scope asserts.
using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.CreateBookingSession;
using Bookings.Features.HoldAvailability;
using Bookings.Jobs;
using Bookings.Outbox;
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
using Transactions.Contracts;
using Transactions.Features.InitiateTransaction;
namespace IntegrationTests.Features.Transactions;

// Initiation used to verify the booking was payable and insert the transaction
// as independent operations, so a cancellation committing in between produced a
// pending payment against a cancelled booking.
//
// On its own that is untidy - no money moves today. What promotes it is what it
// manufactures: if the cancellation also moved an earlier payment to
// RefundPending, the active-transaction check matches nothing and the insert
// succeeds, leaving one booking with a RefundPending *and* a Succeeded
// transaction. The active index permits exactly that pair, and any booking-wide
// query written as SingleOrDefault then throws on every retry and every sweep
// pass for as long as both rows exist.
[Collection("Integration Tests")]
public class PaymentInitiationRaceTests(IntegrationTestWebApplicationFactory factory)
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
            Guid bookingId, DateTimeOffset resolvedAt, CancellationToken cancellationToken) =>
            inner.MarkRefundObligationResolvedAsync(bookingId, resolvedAt, cancellationToken);
    }

    [Fact]
    public async Task ACancellationCommittingMidInitiation_LeavesNoTransactionBehind()
    {
        Unit unit = CreateTestUnit();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
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

        Assert.Empty(await assertScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>()
            .Transactions.AsNoTracking()
            .Where(t => t.BookingId == booking.BookingId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnExpiryDuringInitiationsLockedSection_StepsOverTheBooking()
    {
        // Expiry is the other path that cancels a booking, and it used to hold
        // only the booking row. Initiation never touches that row - it reads
        // the booking through IBookingLookup and holds BookingPaymentLock - so
        // the two could not see each other: initiation re-read Pending, expiry
        // cancelled and released the unit, and initiation then committed a
        // payment against a booking that no longer existed.
        Unit unit = CreateTestUnit();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
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
            await scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>().Bookings
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
                    jobScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>(),
                    jobScope.ServiceProvider.GetRequiredService<IHoldConfirmation>(),
                    jobScope.ServiceProvider.GetRequiredService<ITransactionLookup>(),
                    jobScope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>(),
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

        Booking current = await assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>()
            .Bookings.AsNoTracking()
            .SingleAsync(b => b.Id == booking.BookingId, TestContext.Current.CancellationToken);

        Assert.Equal(BookingStatus.Pending, current.BookingStatus);

        Assert.Single(await assertScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>()
            .Transactions.AsNoTracking()
            .Where(t => t.BookingId == booking.BookingId)
            .ToListAsync(TestContext.Current.CancellationToken));
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
