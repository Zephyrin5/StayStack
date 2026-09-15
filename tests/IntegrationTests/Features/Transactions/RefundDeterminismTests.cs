using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Jobs;
using Bookings.Outbox;
using Bookings.Serialization;
using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NpgsqlTypes;
using Outbox;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using Transactions;
using Transactions.Contracts;
using Transactions.Entities;
using Transactions.Outbox;
using Transactions.Serialization;
namespace IntegrationTests.Features.Transactions;

// Two paths transition Succeeded -> RefundPending and they disagree about the
// amount: the cancellation path knows the policy figure, the delayed
// payment-confirmation path only ever refunds in full. Both were guarded on
// status alone, so whichever outbox dispatch ran first wrote the money and the
// other returned silently.
//
// Identical business history therefore produced 50% or 100% depending on
// scheduling. These pin the amount to the *ordering* of the two events, which
// is what the non-overlapping cases already do: a transaction still Pending at
// cancel time is left alone by the cancellation path, and the later
// confirmation refunds in full.
[Collection("Integration Tests")]
public class RefundDeterminismTests(IntegrationTestWebApplicationFactory factory)
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

    // A paid, confirmed booking whose confirmation message has NOT been
    // delivered - the state the whole defect lives in. Built by hand rather
    // than through the API so the delivery order is the test's to choose.
    private async Task<(Guid BookingId, Guid TransactionId)> SeedPaidButUnconfirmedAsync(
        int daysUntilCheckIn, DateTimeOffset? succeededAt)
    {
        Unit unit = CreateTestUnit();
        Guid bookingId = Guid.CreateVersion7();
        Guid holdId = Guid.CreateVersion7();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(daysUntilCheckIn);

        using IServiceScope scope = factory.Services.CreateScope();

        AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        catalog.AddRange(_pendingProperties);
        _pendingProperties.Clear();
        catalog.Add(unit);
        await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);

        AppBookingsDbContext bookings = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        bookings.UnitAvailabilityHolds.Add(new UnitAvailabilityHold
        {
            Id = holdId,
            UnitId = unit.Id,
            StayRange = new NpgsqlRange<DateOnly>(checkIn, true, checkIn.AddDays(2), false),
            Status = "booked",
            GuestCount = 2,
            CreatedAt = DateTimeOffset.UtcNow,
            BookedAt = succeededAt,
            TotalPrice = Money.Of(200m, Currency.KWD),
            Subtotal = 200m
        });

        Booking booking = Booking.Create(
            bookingId, unit.Id, holdId, null,
            "Jane Guest", "jane@example.com", null,
            checkIn, checkIn.AddDays(2), 2,
            Money.Of(200m, Currency.KWD), Money.Of(200m, Currency.KWD),
            CancellationPolicy.CreateDefault(), "Asia/Kuwait",
            DateTimeOffset.UtcNow.AddMinutes(15));

        bookings.Bookings.Add(booking);
        await bookings.SaveChangesAsync(TestContext.Current.CancellationToken);

        AppTransactionsDbContext transactions =
            scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

        Transaction payment = Transaction.Create(Guid.CreateVersion7(), bookingId, Money.Of(200m, Currency.KWD));

        if (succeededAt is { } paidAt)
        {
            payment.MarkSucceeded(paidAt);
        }

        transactions.Transactions.Add(payment);

        // The confirmation row, committed and deliberately not dispatched -
        // the delay that makes both refund paths reachable at once.
        transactions.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = ConfirmBookingPaymentOutboxMessage.TypeName,
            Payload = System.Text.Json.JsonSerializer.Serialize(
                new ConfirmBookingPaymentOutboxMessage(payment.Id, bookingId),
                TransactionsJsonSerializerContext.Default.ConfirmBookingPaymentOutboxMessage),
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow
        });

        await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (bookingId, payment.Id);
    }

    // The cancellation, enqueued and dispatched by hand so the test decides
    // when it lands relative to the confirmation.
    private async Task CancelAsync(
        Guid bookingId, DateTimeOffset cancelledAt, Money policyRefund, bool dispatchNow = true)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookings = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        Booking booking = await bookings.Bookings
            .SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken);
        booking.Cancel(cancelledAt);

        // The obligation, exactly as CancelBookingHandler writes it. It is what
        // the resolver reads; without it there is nothing to settle and every
        // assertion below would be about a refund that never had a reason to
        // happen.
        bookings.RefundObligations.Add(new RefundObligation
        {
            BookingId = bookingId,
            CancelledAt = cancelledAt,
            PolicyRefundAmount = policyRefund.Amount,
            Currency = policyRefund.Currency,
            Cause = BookingCancellationCause.GuestCancellation
        });

        BookingsOutboxDispatcher dispatcher =
            scope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>();

        OutboxMessage row = dispatcher.Enqueue(
            new ReverseTransactionOutboxMessage(bookingId),
            BookingsJsonSerializerContext.Default.ReverseTransactionOutboxMessage);

        await bookings.SaveChangesAsync(TestContext.Current.CancellationToken);

        if (dispatchNow)
        {
            await dispatcher.TryDispatchAsync(row, TestContext.Current.CancellationToken);
        }
    }

    // The reversal that CancelAsync deliberately left undelivered - what the
    // relay would eventually do.
    private async Task DeliverReversalAsync()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>()
            .DispatchPendingAsync(50, TestContext.Current.CancellationToken);
    }

    private async Task MarkPaymentSucceededAsync(Guid transactionId, DateTimeOffset succeededAt)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppTransactionsDbContext transactions =
            scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

        Transaction payment = await transactions.Transactions
            .SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
        payment.MarkSucceeded(succeededAt);
        await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task DeliverConfirmationAsync()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TransactionsOutboxDispatcher>()
            .DispatchPendingAsync(50, TestContext.Current.CancellationToken);
    }

    private async Task<Transaction> ReadTransactionAsync(Guid transactionId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>()
            .Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
    }

    public enum Ordering
    {
        /// <summary>The cancellation's reversal lands, then the confirmation.</summary>
        ReversalThenConfirmation,

        /// <summary>
        ///     The confirmation lands first, against a booking not yet
        ///     cancelled - so it simply confirms, and the cancellation follows
        ///     normally. Included because it is the ordinary flow and must stay
        ///     unaffected.
        /// </summary>
        ConfirmationBeforeTheCancellation,

        /// <summary>
        ///     The real overlap, and the one the original defect lived in: the
        ///     booking is cancelled and its reversal is enqueued but not yet
        ///     delivered when the confirmation arrives. Both paths then see a
        ///     Succeeded transaction and a Cancelled booking, and before the
        ///     ordering rule the confirmation wrote the full amount first and
        ///     the reversal returned silently.
        /// </summary>
        ConfirmationWhileTheReversalIsStillQueued
    }

    [Theory]
    [InlineData(Ordering.ReversalThenConfirmation)]
    [InlineData(Ordering.ConfirmationBeforeTheCancellation)]
    [InlineData(Ordering.ConfirmationWhileTheReversalIsStillQueued)]
    public async Task APaymentThatPrecededTheCancellation_RefundsThePolicyAmount_WhicheverDispatchRunsFirst(
        Ordering ordering)
    {
        // Paid at 12:00, cancelled at 14:00. The guest bought a stay and then
        // cancelled it, so the cancellation policy decides - and must decide
        // the same way whichever of the two dispatches happens to win.
        DateTimeOffset succeededAt = DateTimeOffset.UtcNow.AddHours(-2);
        DateTimeOffset cancelledAt = DateTimeOffset.UtcNow;

        (Guid bookingId, Guid transactionId) =
            await SeedPaidButUnconfirmedAsync(daysUntilCheckIn: 120, succeededAt);

        Money policyRefund = Money.Of(100m, Currency.KWD);

        switch (ordering)
        {
            case Ordering.ReversalThenConfirmation:
                await CancelAsync(bookingId, cancelledAt, policyRefund);
                await DeliverConfirmationAsync();
                break;

            case Ordering.ConfirmationBeforeTheCancellation:
                await DeliverConfirmationAsync();
                await CancelAsync(bookingId, cancelledAt, policyRefund);
                break;

            case Ordering.ConfirmationWhileTheReversalIsStillQueued:
                await CancelAsync(bookingId, cancelledAt, policyRefund, dispatchNow: false);
                await DeliverConfirmationAsync();
                await DeliverReversalAsync();
                break;
        }

        Transaction settled = await ReadTransactionAsync(transactionId);

        // 100, never 200. Before the ordering rule this was 200 in one branch
        // of this theory and 100 in the other.
        Assert.Equal(100m, settled.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.GuestCancellation, settled.RefundCause);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APaymentThatFollowedTheCancellation_RefundsInFull_WhicheverDispatchRunsFirst(
        bool reverseBeforeThePaymentSucceeds)
    {
        // Cancelled at 12:00, paid at 14:00. That payment bought nothing, so
        // the whole amount goes back - no cancellation fee applies to a stay
        // the guest no longer had.
        //
        // Built as a genuine ordering rather than by backdating a timestamp.
        // Setting CancelledAt in the past but cancelling afterwards would let
        // the confirmation arrive at a still-live booking and confirm it, and no
        // refund path would run - a scenario that cannot happen.
        //
        // What actually varies here is whether the cancellation's reversal
        // dispatch runs before or after the payment succeeds. Both must reach
        // the same figure.
        DateTimeOffset cancelledAt = DateTimeOffset.UtcNow.AddHours(-2);
        DateTimeOffset succeededAt = DateTimeOffset.UtcNow;

        (Guid bookingId, Guid transactionId) =
            await SeedPaidButUnconfirmedAsync(daysUntilCheckIn: 121, succeededAt: null);

        Money policyRefund = Money.Of(100m, Currency.KWD);

        if (reverseBeforeThePaymentSucceeds)
        {
            // The reversal finds the transaction still Pending and leaves it
            // alone - the pre-existing behaviour, unchanged.
            await CancelAsync(bookingId, cancelledAt, policyRefund);
            await MarkPaymentSucceededAsync(transactionId, succeededAt);
        }
        else
        {
            // The reversal finds it Succeeded and must decline anyway, because
            // the payment landed after the cancellation. This is the branch the
            // ordering rule adds; before it, the policy figure was written here
            // whenever this dispatch happened to come second.
            await MarkPaymentSucceededAsync(transactionId, succeededAt);
            await CancelAsync(bookingId, cancelledAt, policyRefund);
        }

        await DeliverConfirmationAsync();

        Transaction settled = await ReadTransactionAsync(transactionId);

        Assert.Equal(200m, settled.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.PaymentUnusable, settled.RefundCause);
    }

    [Fact]
    public async Task WithEveryOutboxMessageDroppedOnTheFloor_TheBackstopStillRefunds()
    {
        // Correctness does not depend on delivery.
        //
        // Nothing is dispatched here at all - not the reversal, not the
        // confirmation. The obligation is a durable work item, so the sweep
        // finds it and the refund is still recorded.
        DateTimeOffset succeededAt = DateTimeOffset.UtcNow.AddHours(-2);
        DateTimeOffset cancelledAt = DateTimeOffset.UtcNow.AddHours(-1);

        (Guid bookingId, Guid transactionId) =
            await SeedPaidButUnconfirmedAsync(daysUntilCheckIn: 123, succeededAt);

        await CancelAsync(bookingId, cancelledAt, Money.Of(100m, Currency.KWD), dispatchNow: false);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            // The clock is moved past the job's grace period rather than the
            // grace being shortened: the job exists to catch obligations the
            // messages did not, and waiting is how it tells those apart.
            FakeTimeProvider clock = new FakeTimeProvider();
            clock.SetUtcNow(DateTimeOffset.UtcNow.AddMinutes(10));

            ResolveOutstandingRefundsJob job = new ResolveOutstandingRefundsJob(
                scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>(),
                scope.ServiceProvider.GetRequiredService<ITransactionReversal>(),
                clock,
                NullLogger<ResolveOutstandingRefundsJob>.Instance);

            await job.ResolveAsync(null!, TestContext.Current.CancellationToken);
        }

        Transaction settled = await ReadTransactionAsync(transactionId);

        Assert.Equal(100m, settled.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.GuestCancellation, settled.RefundCause);

        // And the obligation is marked, so the sweep stops revisiting it.
        using IServiceScope assertScope = factory.Services.CreateScope();
        RefundObligation obligation = await assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>()
            .RefundObligations.AsNoTracking()
            .SingleAsync(o => o.BookingId == bookingId, TestContext.Current.CancellationToken);

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
        DateTimeOffset succeededAt = DateTimeOffset.UtcNow.AddHours(-2);
        DateTimeOffset cancelledAt = DateTimeOffset.UtcNow.AddHours(-1);

        (Guid bookingId, Guid transactionId) =
            await SeedPaidButUnconfirmedAsync(daysUntilCheckIn: 124, succeededAt);

        await CancelAsync(bookingId, cancelledAt, Money.Of(100m, Currency.KWD), dispatchNow: false);

        // A thousand older obligations with no payment behind any of them -
        // exactly the per-run cap, so a sweep ordered by age alone would fill
        // every batch with them and never reach the payable one above.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppBookingsDbContext bookings = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

            for (int i = 0; i < 1000; i++)
            {
                bookings.RefundObligations.Add(new RefundObligation
                {
                    BookingId = Guid.CreateVersion7(),
                    CancelledAt = cancelledAt.AddHours(-2).AddSeconds(i),
                    NextAttemptAt = cancelledAt.AddHours(-2).AddSeconds(i),
                    PolicyRefundAmount = 50m,
                    Currency = Currency.KWD,
                    Cause = BookingCancellationCause.GuestCancellation
                });
            }

            await bookings.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            FakeTimeProvider clock = new FakeTimeProvider();
            clock.SetUtcNow(DateTimeOffset.UtcNow.AddMinutes(10));

            ResolveOutstandingRefundsJob job = new ResolveOutstandingRefundsJob(
                scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>(),
                scope.ServiceProvider.GetRequiredService<ITransactionReversal>(),
                clock,
                NullLogger<ResolveOutstandingRefundsJob>.Instance);

            // Twice. The first run is allowed to spend its whole window on the
            // backlog - what must not happen is that every future run does too,
            // which is what backing the unpayable rows off prevents.
            await job.ResolveAsync(null!, TestContext.Current.CancellationToken);
            await job.ResolveAsync(null!, TestContext.Current.CancellationToken);
        }

        Transaction settled = await ReadTransactionAsync(transactionId);

        Assert.Equal(100m, settled.RefundAmount!.Value.Amount);
    }

    [Fact]
    public async Task ARefundAlreadyRecorded_IsNeverOverwritten()
    {
        // The backstop under the ordering rule. If both paths ever decided they
        // owned the same case, the second must be a no-op rather than a silent
        // replacement - nothing downstream would show that the amount had
        // changed.
        DateTimeOffset succeededAt = DateTimeOffset.UtcNow.AddHours(-2);

        (Guid bookingId, Guid transactionId) =
            await SeedPaidButUnconfirmedAsync(daysUntilCheckIn: 122, succeededAt);

        await CancelAsync(bookingId, DateTimeOffset.UtcNow, Money.Of(100m, Currency.KWD));

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppTransactionsDbContext transactions =
                scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

            Transaction recorded = await transactions.Transactions
                .SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);

            // Forced past the ordering rule, straight at the entity, because
            // the rule is what makes this unreachable through the two callers -
            // and the guard has to hold even when it is.
            recorded.MarkRefundPending(Money.Of(200m, Currency.KWD), RefundCause.PaymentUnusable);
            await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Transaction settled = await ReadTransactionAsync(transactionId);

        Assert.Equal(100m, settled.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.GuestCancellation, settled.RefundCause);
    }
}
