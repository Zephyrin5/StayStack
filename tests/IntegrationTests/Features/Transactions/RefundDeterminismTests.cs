// Proves the refund amount follows the order of payment and cancellation through the real
// handlers, that the sweep refunds an obligation with a payment behind it, that a backlog of unpaid
// obligations does not starve it, and that a recorded refund is not overwritten. The two
// concurrency tests pin each interleaving with a barrier on the booking's row lock and assert the
// waiting side holds no lock on transactions; see their comments for what breaking the lock order
// does.
using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Features.CancelBooking;
using Bookings.Jobs;
using BuildingBlocks.Identity;
using BuildingBlocks.Persistence;
using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using Transactions;
using Transactions.Contracts;
using Transactions.Entities;
using Transactions.Features.MarkTransactionSucceeded;
namespace IntegrationTests.Features.Transactions;

// Two facts decide a refund: when the payment succeeded and when the booking was
// cancelled. Paid first, the guest bought a stay and cancelled it, so the
// cancellation policy decides. Cancelled first, the payment bought nothing and
// comes back in full. The amount must follow that order, however the two
// requests interleave.
//
// Check-in is three days out, inside the default policy's 50% tier, so the
// policy figure (100) and the full amount (200) differ.
[Collection("Integration Tests")]
public class RefundDeterminismTests(IntegrationTestWebApplicationFactory factory)
{
    private const int DaysUntilCheckIn = 3;

    private sealed class Customer(Guid id) : ICurrentUserProvider
    {
        public Guid? UserId => id;
        public Guid? HostId => null;
        public IReadOnlyCollection<string> Roles => [];
    }

    private sealed class NoSession : IBookingSessions
    {
        public BookingSession Issue(Guid bookingId) => throw new NotSupportedException();
        public Task<Guid?> GetSessionBookingIdAsync(CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
    }

    private sealed record Seeded(Guid BookingId, Guid CustomerId, Guid TransactionId);

    // A checkout awaiting payment - a Pending booking, its hold in
    // 'pending_payment', and a Pending transaction - owned by a customer.
    private async Task<Seeded> SeedAwaitingPaymentAsync()
    {
        Property property = CatalogSeeding.CreateProperty();
        Unit unit = Unit.Create(
            Guid.CreateVersion7(),
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            100m);

        Guid bookingId = Guid.CreateVersion7();
        Guid holdId = Guid.CreateVersion7();
        Guid customerId = Guid.NewGuid();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(DaysUntilCheckIn);

        using IServiceScope scope = factory.Services.CreateScope();

        CatalogDb catalog = scope.ServiceProvider.GetRequiredService<CatalogDb>();
        catalog.AddRange(property, unit);
        await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);

        BookingsDb bookings = scope.ServiceProvider.GetRequiredService<BookingsDb>();
        bookings.UnitAvailabilityHolds.Add(new UnitAvailabilityHold
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

        bookings.Bookings.Add(Booking.Create(
            bookingId, unit.Id, holdId, customerId,
            "Jane Guest", "jane@example.com", null,
            checkIn, checkIn.AddDays(2), 2,
            Money.Of(200m, Currency.KWD), Money.Of(200m, Currency.KWD),
            CancellationPolicy.CreateDefault(), "Asia/Kuwait",
            DateTimeOffset.UtcNow.AddMinutes(15)));

        await bookings.SaveChangesAsync(TestContext.Current.CancellationToken);

        TransactionsDb transactions = scope.ServiceProvider.GetRequiredService<TransactionsDb>();
        Transaction payment = Transaction.Create(Guid.CreateVersion7(), bookingId, Money.Of(200m, Currency.KWD));
        transactions.Transactions.Add(payment);
        await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new Seeded(bookingId, customerId, payment.Id);
    }

    // The real handlers, resolved from a fresh scope. An override builds a
    // replacement for one dependency from that same scope - which is how the
    // concurrency tests pause one side without taking its collaborator off the
    // handler's DbContext.
    private async Task<CancelBookingResponse> CancelAsync(Seeded seeded, Func<IServiceProvider, object>? @override = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        object[] arguments = @override is null
            ? [new Customer(seeded.CustomerId), new NoSession()]
            : [new Customer(seeded.CustomerId), new NoSession(), @override(scope.ServiceProvider)];
        CancelBookingHandler handler = ActivatorUtilities.CreateInstance<CancelBookingHandler>(scope.ServiceProvider, arguments);

        return await handler.Handle(
            new CancelBookingRequest { BookingId = seeded.BookingId }, TestContext.Current.CancellationToken);
    }

    private async Task<MarkTransactionSucceededResponse> SucceedAsync(Seeded seeded, Func<IServiceProvider, object>? @override = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        MarkTransactionSucceededHandler handler = ActivatorUtilities.CreateInstance<MarkTransactionSucceededHandler>(
            scope.ServiceProvider, @override is null ? [] : [@override(scope.ServiceProvider)]);

        return await handler.Handle(
            new MarkTransactionSucceededRequest { TransactionId = seeded.TransactionId }, TestContext.Current.CancellationToken);
    }

    private async Task<(Transaction Payment, Booking Booking, RefundObligation Obligation)> ReadAsync(Seeded seeded)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        Transaction payment = await scope.ServiceProvider.GetRequiredService<TransactionsDb>()
            .Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == seeded.TransactionId, TestContext.Current.CancellationToken);

        BookingsDb bookings = scope.ServiceProvider.GetRequiredService<BookingsDb>();
        Booking booking = await bookings.Bookings.AsNoTracking()
            .SingleAsync(b => b.Id == seeded.BookingId, TestContext.Current.CancellationToken);
        RefundObligation obligation = await bookings.RefundObligations.AsNoTracking()
            .SingleAsync(o => o.BookingId == seeded.BookingId, TestContext.Current.CancellationToken);

        return (payment, booking, obligation);
    }

    [Fact]
    public async Task APaymentThatPrecededTheCancellation_RefundsThePolicyAmount()
    {
        Seeded seeded = await SeedAwaitingPaymentAsync();

        MarkTransactionSucceededResponse paid = await SucceedAsync(seeded);
        Assert.Equal(TransactionStatus.Succeeded, paid.TransactionStatus);

        await CancelAsync(seeded);

        (Transaction payment, Booking booking, RefundObligation obligation) = await ReadAsync(seeded);

        Assert.Equal(BookingStatus.Cancelled, booking.BookingStatus);
        Assert.Equal(TransactionStatus.RefundPending, payment.TransactionStatus);
        Assert.Equal(100m, payment.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.GuestCancellation, payment.RefundCause);
        Assert.NotNull(obligation.ResolvedAt);
    }

    [Fact]
    public async Task APaymentThatFollowedTheCancellation_RefundsInFull()
    {
        // No cancellation fee applies to a stay the guest no longer had.
        Seeded seeded = await SeedAwaitingPaymentAsync();

        await CancelAsync(seeded);

        MarkTransactionSucceededResponse paid = await SucceedAsync(seeded);
        Assert.Equal(TransactionStatus.RefundPending, paid.TransactionStatus);

        (Transaction payment, Booking booking, RefundObligation obligation) = await ReadAsync(seeded);

        Assert.Equal(BookingStatus.Cancelled, booking.BookingStatus);
        Assert.Equal(200m, payment.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.PaymentUnusable, payment.RefundCause);
        Assert.NotNull(obligation.ResolvedAt);
    }

    // ---- concurrent cancellation and payment ------------------------------

    // Pauses the cancellation after it holds the booking's row lock, before it
    // releases the hold and resolves the refund.
    private sealed class PauseBeforeRelease(IHoldConfirmation inner, TaskCompletionSource reached, TaskCompletionSource gate)
        : IHoldConfirmation
    {
        public Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken) =>
            inner.ConfirmHoldAsync(holdId, cancellationToken);

        public Task<bool> MarkHoldPaidAsync(Guid holdId, CancellationToken cancellationToken) =>
            inner.MarkHoldPaidAsync(holdId, cancellationToken);

        public async Task ReleaseHoldAsync(Guid holdId, CancellationToken cancellationToken)
        {
            reached.TrySetResult();
            await gate.Task;
            await inner.ReleaseHoldAsync(holdId, cancellationToken);
        }
    }

    // Pauses the payment after it has confirmed the booking under its row lock,
    // before it writes the transaction.
    private sealed class PauseAfterConfirming(
        IBookingPaymentConfirmation inner, TaskCompletionSource reached, TaskCompletionSource gate)
        : IBookingPaymentConfirmation
    {
        public async Task<bool> ConfirmPaymentAsync(Guid bookingId, CancellationToken cancellationToken)
        {
            bool confirmed = await inner.ConfirmPaymentAsync(bookingId, cancellationToken);
            reached.TrySetResult();
            await gate.Task;
            return confirmed;
        }
    }

    private static (TaskCompletionSource Reached, TaskCompletionSource Gate) NewGate() =>
        (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    // The barrier for the blocked side: the backend waiting on the booking's row
    // lock, found through Postgres rather than a guessed delay.
    private async Task<int> WaitForABookingRowLockWaiterAsync()
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        while (true)
        {
            await using NpgsqlCommand command = new NpgsqlCommand(
                """
                SELECT pid FROM pg_stat_activity
                WHERE wait_event_type = 'Lock' AND query LIKE '%FROM "bookings" WHERE id = % FOR UPDATE%'
                LIMIT 1
                """, connection);

            if (await command.ExecuteScalarAsync(timeout.Token) is int pid)
            {
                return pid;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    // Whether a backend has written to the transactions table in its open
    // transaction. A writer waiting on the booking lock while holding a
    // transaction row is half of a deadlock with a cancellation that holds the
    // booking lock and goes on to resolve the refund.
    private async Task<bool> HoldsATransactionsWriteLockAsync(int pid)
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using NpgsqlCommand command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_locks l JOIN pg_class c ON c.oid = l.relation
                WHERE l.pid = @Pid AND c.relname = 'transactions' AND l.mode = 'RowExclusiveLock')
            """, connection);
        command.Parameters.AddWithValue("Pid", pid);

        return (bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task APaymentArrivingWhileTheCancellationHoldsTheBooking_WaitsWithoutATransactionLock_AndIsRefundedInFull()
    {
        // The cancellation holds the booking's row lock. The payment must queue
        // on that lock before it writes its transaction row (docs/adr/0028):
        // holding the transaction row while waiting would put it one step from a
        // deadlock with a cancellation resolving the refund.
        //
        // Breaking the order - saving the transaction before ConfirmPaymentAsync
        // - fails the lock assertion below. It does not produce an actual
        // deadlock here: the cancellation's resolver reads the committed
        // Pending row under Read Committed and writes nothing.
        Seeded seeded = await SeedAwaitingPaymentAsync();
        (TaskCompletionSource reached, TaskCompletionSource gate) = NewGate();

        Task<CancelBookingResponse> cancellation = CancelAsync(seeded, services => new PauseBeforeRelease(
            services.GetRequiredService<IHoldConfirmation>(), reached, gate));

        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Task<MarkTransactionSucceededResponse> payment = SucceedAsync(seeded);
        int waiter = await WaitForABookingRowLockWaiterAsync();

        Assert.False(await HoldsATransactionsWriteLockAsync(waiter),
            "The payment wrote its transaction row before taking the booking lock.");

        gate.SetResult();

        await cancellation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        MarkTransactionSucceededResponse paid =
            await payment.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(TransactionStatus.RefundPending, paid.TransactionStatus);

        (Transaction settled, Booking booking, RefundObligation obligation) = await ReadAsync(seeded);

        Assert.Equal(BookingStatus.Cancelled, booking.BookingStatus);
        Assert.Equal(200m, settled.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.PaymentUnusable, settled.RefundCause);
        Assert.NotNull(obligation.ResolvedAt);
    }

    [Fact]
    public async Task ACancellationArrivingWhileThePaymentHoldsTheBooking_WaitsForIt_AndRefundsThePolicyAmount()
    {
        // The mirror: the payment holds the booking's row lock, the cancellation
        // queues behind it, and then finds a Confirmed booking with a Succeeded
        // payment to refund at the policy figure.
        Seeded seeded = await SeedAwaitingPaymentAsync();
        (TaskCompletionSource reached, TaskCompletionSource gate) = NewGate();

        Task<MarkTransactionSucceededResponse> payment = SucceedAsync(seeded, services => new PauseAfterConfirming(
            services.GetRequiredService<IBookingPaymentConfirmation>(), reached, gate));

        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Task<CancelBookingResponse> cancellation = CancelAsync(seeded);
        int waiter = await WaitForABookingRowLockWaiterAsync();

        Assert.False(await HoldsATransactionsWriteLockAsync(waiter),
            "The cancellation wrote to transactions before taking the booking lock.");

        gate.SetResult();

        MarkTransactionSucceededResponse paid =
            await payment.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cancellation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(TransactionStatus.Succeeded, paid.TransactionStatus);

        (Transaction settled, Booking booking, RefundObligation obligation) = await ReadAsync(seeded);

        Assert.Equal(BookingStatus.Cancelled, booking.BookingStatus);
        Assert.Equal(100m, settled.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.GuestCancellation, settled.RefundCause);
        Assert.NotNull(obligation.ResolvedAt);
    }

    // ---- the sweep ---------------------------------------------------------

    // A cancelled booking with an unresolved obligation and a Succeeded payment
    // behind it. Both handlers resolve inline, so this is written directly: it is
    // what the sweep exists to catch if an inline resolution ever does not record
    // the refund.
    private async Task<Seeded> SeedUnresolvedWithAPaymentAsync()
    {
        Seeded seeded = await SeedAwaitingPaymentAsync();
        DateTimeOffset succeededAt = DateTimeOffset.UtcNow.AddHours(-2);
        DateTimeOffset cancelledAt = DateTimeOffset.UtcNow.AddHours(-1);

        using IServiceScope scope = factory.Services.CreateScope();

        TransactionsDb transactions = scope.ServiceProvider.GetRequiredService<TransactionsDb>();
        Transaction payment = await transactions.Transactions
            .SingleAsync(t => t.Id == seeded.TransactionId, TestContext.Current.CancellationToken);
        payment.MarkSucceeded(succeededAt);
        await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);

        BookingsDb bookings = scope.ServiceProvider.GetRequiredService<BookingsDb>();
        Booking booking = await bookings.Bookings
            .SingleAsync(b => b.Id == seeded.BookingId, TestContext.Current.CancellationToken);
        booking.Cancel(cancelledAt);
        bookings.RefundObligations.Add(new RefundObligation
        {
            BookingId = seeded.BookingId,
            CancelledAt = cancelledAt,
            PolicyRefundAmount = 100m,
            Currency = Currency.KWD,
            Cause = BookingCancellationCause.GuestCancellation,
            NextAttemptAt = cancelledAt
        });
        await bookings.SaveChangesAsync(TestContext.Current.CancellationToken);

        return seeded;
    }

    // The clock is moved past the job's grace period rather than the grace being
    // shortened.
    private async Task RunTheSweepAsync(int runs = 1)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        FakeTimeProvider clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UtcNow.AddMinutes(10));

        ResolveOutstandingRefundsJob job = ActivatorUtilities.CreateInstance<ResolveOutstandingRefundsJob>(
            scope.ServiceProvider, clock, NullLogger<ResolveOutstandingRefundsJob>.Instance);

        for (int run = 0; run < runs; run++)
        {
            await job.ResolveAsync(null!, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AnUnresolvedObligationWithAPaymentBehindIt_IsRefundedByTheSweep()
    {
        Seeded seeded = await SeedUnresolvedWithAPaymentAsync();

        await RunTheSweepAsync();

        (Transaction settled, _, RefundObligation obligation) = await ReadAsync(seeded);

        Assert.Equal(100m, settled.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.GuestCancellation, settled.RefundCause);

        // And the obligation is marked, so the sweep stops revisiting it.
        Assert.NotNull(obligation.ResolvedAt);
    }

    [Fact]
    public async Task ABacklogOfUnpaidObligations_DoesNotStarveOneWithAPaymentBehindIt()
    {
        // The sweep's ordinary workload, not an error condition. Both writers
        // record an obligation whether or not anyone paid, and for an unpaid
        // booking the resolver correctly does nothing - so the row stays
        // unresolved. Ordered by CancelledAt and capped at 1000, a backlog of
        // those held the front of the queue permanently and the sweep never
        // reached a newer obligation that actually had money against it.
        //
        // Most cancellations are of unpaid bookings, so this is the steady
        // state rather than a spike.
        Seeded seeded = await SeedUnresolvedWithAPaymentAsync();
        DateTimeOffset older = DateTimeOffset.UtcNow.AddHours(-3);

        // A thousand older obligations with no payment behind any of them -
        // exactly the per-run cap, so a sweep ordered by age alone would fill
        // every batch with them and never reach the payable one above.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            BookingsDb bookings = scope.ServiceProvider.GetRequiredService<BookingsDb>();

            for (int i = 0; i < 1000; i++)
            {
                bookings.RefundObligations.Add(new RefundObligation
                {
                    BookingId = Guid.CreateVersion7(),
                    CancelledAt = older.AddSeconds(i),
                    NextAttemptAt = older.AddSeconds(i),
                    PolicyRefundAmount = 50m,
                    Currency = Currency.KWD,
                    Cause = BookingCancellationCause.GuestCancellation
                });
            }

            await bookings.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Twice. The first run is allowed to spend its whole window on the
        // backlog - what must not happen is that every future run does too,
        // which is what backing the unpayable rows off prevents.
        await RunTheSweepAsync(runs: 2);

        (Transaction settled, _, _) = await ReadAsync(seeded);

        Assert.Equal(100m, settled.RefundAmount!.Value.Amount);
    }

    [Fact]
    public async Task ARefundAlreadyRecorded_IsNeverOverwritten()
    {
        // If two paths ever decided they owned the same case, the second must be
        // a no-op rather than a silent replacement - nothing downstream would
        // show that the amount had changed.
        Seeded seeded = await SeedAwaitingPaymentAsync();

        await SucceedAsync(seeded);
        await CancelAsync(seeded);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            TransactionsDb transactions =
                scope.ServiceProvider.GetRequiredService<TransactionsDb>();

            Transaction recorded = await transactions.Transactions
                .SingleAsync(t => t.Id == seeded.TransactionId, TestContext.Current.CancellationToken);

            // Forced past the resolver, straight at the entity, because the
            // resolver is what makes this unreachable - and the guard has to hold
            // even when it is.
            recorded.MarkRefundPending(Money.Of(200m, Currency.KWD), RefundCause.PaymentUnusable);
            await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (Transaction settled, _, _) = await ReadAsync(seeded);

        Assert.Equal(100m, settled.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.GuestCancellation, settled.RefundCause);
    }
}
