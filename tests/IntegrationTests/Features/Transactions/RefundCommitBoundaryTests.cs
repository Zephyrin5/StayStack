// Proves refund resolution finishes from seeded states it does not produce itself, and from a
// status flip between the resolver's read and write inside its atomic scope; TwoResolversRacing
// fails 6/6 with the concurrency catch disabled. APaymentStateRead_DescribesOneMomentRatherThanTwo
// reads a static row and cannot tell one read from two.
using Bookings;
using BuildingBlocks.Persistence;
using Bookings.Contracts;
using Bookings.Entities;
using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using Transactions;
using Transactions.Contracts;
using Transactions.Entities;
using System.Data;
namespace IntegrationTests.Features.Transactions;

// The refund decision writes two modules' rows: the amount lands on the
// Transaction, and the obligation's ResolvedAt marker lands in Bookings. They
// commit in one atomic scope. The transaction status stays the authority on
// whether a refund exists, so these pin the resolver against a marker and a
// refund that disagree - states seeded directly, since the resolver does not
// produce them - and against a concurrent resolver. RefundDeterminismTests
// covers the order of cancellation and payment.
[Collection("Integration Tests")]
public class RefundCommitBoundaryTests(IntegrationTestWebApplicationFactory factory)
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

    /// <summary>
    ///     A paid booking that has been cancelled, with its obligation written -
    ///     the state every test here starts from. Nothing is resolved.
    /// </summary>
    private async Task<(Guid BookingId, Guid TransactionId)> SeedCancelledAndPaidAsync(int daysUntilCheckIn)
    {
        Unit unit = CreateTestUnit();
        Guid bookingId = Guid.CreateVersion7();
        Guid holdId = Guid.CreateVersion7();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(daysUntilCheckIn);

        DateTimeOffset succeededAt = DateTimeOffset.UtcNow.AddHours(-2);
        DateTimeOffset cancelledAt = DateTimeOffset.UtcNow.AddHours(-1);

        using IServiceScope scope = factory.Services.CreateScope();

        CatalogDb catalog = scope.ServiceProvider.GetRequiredService<CatalogDb>();
        catalog.AddRange(_pendingProperties);
        _pendingProperties.Clear();
        catalog.Add(unit);
        await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);

        BookingsDb bookings = scope.ServiceProvider.GetRequiredService<BookingsDb>();
        bookings.UnitAvailabilityHolds.Add(new UnitAvailabilityHold
        {
            Id = holdId,
            UnitId = unit.Id,
            StayRange = new NpgsqlRange<DateOnly>(checkIn, true, checkIn.AddDays(2), false),
            Status = "booked",
            GuestCount = 2,
            CreatedAt = DateTimeOffset.UtcNow,
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

        booking.Cancel(cancelledAt, new BookingPaymentLockHandle(booking.Id));
        bookings.Bookings.Add(booking);

        bookings.RefundObligations.Add(new RefundObligation
        {
            BookingId = bookingId,
            CancelledAt = cancelledAt,
            PolicyRefundAmount = 100m,
            Currency = Currency.KWD,
            Cause = BookingCancellationCause.GuestCancellation
        });

        await bookings.SaveChangesAsync(TestContext.Current.CancellationToken);

        TransactionsDb transactions =
            scope.ServiceProvider.GetRequiredService<TransactionsDb>();

        Transaction payment = Transaction.Create(Guid.CreateVersion7(), bookingId, Money.Of(200m, Currency.KWD));
        payment.MarkSucceeded(succeededAt);
        transactions.Transactions.Add(payment);
        await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (bookingId, payment.Id);
    }

    private async Task ResolveAsync(Guid bookingId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await ResolveInScopeAsync(scope, reversal => reversal.ResolveRefundAsync(bookingId, TestContext.Current.CancellationToken));
    }

    // The resolver runs only inside a transaction, as ResolveOutstandingRefundsJob calls it.
    private static Task<decimal?> ResolveInScopeAsync(IServiceScope scope, Func<TransactionReversal, Task<decimal?>> resolve) =>
        scope.ServiceProvider.GetRequiredService<ITransactionRunner>().ExecuteAsync(
            IsolationLevel.ReadCommitted,
            _ => resolve(scope.ServiceProvider.GetRequiredService<TransactionReversal>()),
            TestContext.Current.CancellationToken);

    private async Task<(Transaction Payment, RefundObligation Obligation)> ReadAsync(
        Guid bookingId, Guid transactionId)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        Transaction payment = await scope.ServiceProvider.GetRequiredService<TransactionsDb>()
            .Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);

        RefundObligation obligation = await scope.ServiceProvider.GetRequiredService<BookingsDb>()
            .RefundObligations.AsNoTracking()
            .SingleAsync(o => o.BookingId == bookingId, TestContext.Current.CancellationToken);

        return (payment, obligation);
    }

    [Fact]
    public async Task AResolvedMarkerWithNoRefundBehindIt_DoesNotSuppressTheRefund()
    {
        // A marker with no refund behind it. The resolver commits the two
        // together, so the state is written directly; what this pins is that the
        // transaction status, not the marker, decides whether a refund is owed.
        (Guid bookingId, Guid transactionId) = await SeedCancelledAndPaidAsync(daysUntilCheckIn: 160);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBookingLookup>()
                .MarkRefundObligationResolvedAsync(
                    bookingId, DateTimeOffset.UtcNow, RefundObligationOutcome.RefundRecorded,
                    TestContext.Current.CancellationToken);
        }

        // Act
        await ResolveAsync(bookingId);

        // Assert - the refund happened. Treating ResolvedAt as authority would
        // return null here, and the sweep would skip the row on
        // ResolvedAt != null: lost permanently, with every mechanism reporting
        // success.
        (Transaction payment, RefundObligation obligation) = await ReadAsync(bookingId, transactionId);

        Assert.Equal(TransactionStatus.RefundPending, payment.TransactionStatus);
        Assert.Equal(100m, payment.RefundAmount!.Value.Amount);
        Assert.NotNull(obligation.ResolvedAt);
    }

    [Fact]
    public async Task ARefundWithNoMarkerBehindIt_FinishesTheBookkeeping()
    {
        // The mirror: a refund already recorded and an obligation not yet
        // marked, as a refund recorded before the booking was cancelled leaves
        // it.
        //
        // Querying for Succeeded alone would find nothing and return at the
        // first step, so the obligation would stay unresolved forever and the
        // sweep re-process it every cycle.
        (Guid bookingId, Guid transactionId) = await SeedCancelledAndPaidAsync(daysUntilCheckIn: 161);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            TransactionsDb transactions =
                scope.ServiceProvider.GetRequiredService<TransactionsDb>();

            Transaction payment = await transactions.Transactions
                .SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);

            payment.MarkRefundPending(Money.Of(100m, Currency.KWD), RefundCause.GuestCancellation);
            await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await ResolveAsync(bookingId);

        (Transaction settled, RefundObligation obligation) = await ReadAsync(bookingId, transactionId);

        // The obligation is settled, and the refund was not written twice.
        Assert.NotNull(obligation.ResolvedAt);
        Assert.Equal(100m, settled.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.GuestCancellation, settled.RefundCause);
    }

    // Moves the transaction to RefundPending from a *different* scope, at the
    // one moment that puts the resolver into its concurrency catch.
    //
    // The resolver reads the transaction, then asks for the obligation, then
    // writes. Flipping the row during that middle call means its own write is
    // made from a now-stale tracked entity - the in-memory status is still
    // Succeeded so MarkRefundPending's guard passes, and the xmin token catches
    // it at SaveChanges. That is the branch under test, and nothing else
    // reaches it deterministically: a transaction already RefundPending on
    // entry short-circuits at step 2 and never gets near the catch.
    private sealed class RefundTheTransactionMidResolve(
        IBookingLookup inner, IServiceScopeFactory scopes, Guid transactionId) : IBookingLookup
    {
        private int _fired;

        public async Task<RefundObligationSnapshot?> GetRefundObligationAsync(
            Guid bookingId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _fired) == 1)
            {
                using IServiceScope scope = scopes.CreateScope();
                TransactionsDb transactions =
                    scope.ServiceProvider.GetRequiredService<TransactionsDb>();

                Transaction payment = await transactions.Transactions
                    .SingleAsync(t => t.Id == transactionId, cancellationToken);

                payment.MarkRefundPending(Money.Of(100m, Currency.KWD), RefundCause.GuestCancellation);
                await transactions.SaveChangesAsync(cancellationToken);
            }

            return await inner.GetRefundObligationAsync(bookingId, cancellationToken);
        }

        public Task<BookingSummary?> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
            inner.GetBookingAsync(bookingId, cancellationToken);

        public Task<BookingAccessResult?> VerifyBookingAccessAsync(
            Guid bookingId, Guid? customerId, CancellationToken cancellationToken) =>
            inner.VerifyBookingAccessAsync(bookingId, customerId, cancellationToken);

        public Task<IReadOnlyList<BookingAccessResult>> GetConfirmedBookingsForCustomerAsync(
            Guid customerId, DateOnly checkOutFrom, DateOnly checkOutTo, CancellationToken cancellationToken) =>
            inner.GetConfirmedBookingsForCustomerAsync(customerId, checkOutFrom, checkOutTo, cancellationToken);

        public Task<BookingAccessResult?> GetBookingDetailsAsync(Guid bookingId, CancellationToken cancellationToken) =>
            inner.GetBookingDetailsAsync(bookingId, cancellationToken);

        public Task MarkRefundObligationResolvedAsync(
            Guid bookingId, DateTimeOffset resolvedAt, RefundObligationOutcome outcome, CancellationToken cancellationToken) =>
            inner.MarkRefundObligationResolvedAsync(bookingId, resolvedAt, outcome, cancellationToken);
    }

    [Fact]
    public async Task AResolverLosingTheRaceInsideItsScope_StillSettlesTheObligation()
    {
        // The concurrency catch, inside the atomic scope it runs in. The loser's
        // UPDATE carries a stale xmin after the other resolver committed, so it
        // matches no row - EF reports that as a concurrency failure, but no
        // statement failed and the scope's transaction stays usable. The
        // resolver re-reads the row and marks the obligation in the same
        // commit.
        (Guid bookingId, Guid transactionId) = await SeedCancelledAndPaidAsync(daysUntilCheckIn: 163);

        using (IServiceScope hostScope = factory.WithWebHostBuilder(builder =>
                   builder.ConfigureServices(services =>
                   {
                       ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IBookingLookup));
                       services.Remove(original);
                       services.AddScoped<IBookingLookup>(sp => new RefundTheTransactionMidResolve(
                           (IBookingLookup)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!),
                           sp.GetRequiredService<IServiceScopeFactory>(),
                           transactionId));
                   })).Services.CreateScope())
        {
            decimal? recorded = await ResolveInScopeAsync(
                hostScope, reversal => reversal.ResolveRefundAsync(bookingId, TestContext.Current.CancellationToken));

            // The other resolver recorded it; this one did not.
            Assert.Null(recorded);
        }

        (Transaction payment, RefundObligation obligation) = await ReadAsync(bookingId, transactionId);

        Assert.Equal(TransactionStatus.RefundPending, payment.TransactionStatus);
        Assert.Equal(100m, payment.RefundAmount!.Value.Amount);
        Assert.NotNull(obligation.ResolvedAt);
    }

    [Fact]
    public async Task ARefundPendingAttemptBesideASucceededOne_ResolvesTheRightOne()
    {
        // A state the schema permits. The active-transaction index filters
        // transaction_status IN ('Pending','Succeeded'), so a RefundPending
        // attempt and a Succeeded one coexist legally, and SingleOrDefault over
        // "this booking's transactions" would throw on every retry and every
        // sweep pass for as long as both rows exist.
        //
        // Reachable in practice from an initiation racing a cancellation.
        (Guid bookingId, Guid refundedId) = await SeedCancelledAndPaidAsync(daysUntilCheckIn: 164);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            TransactionsDb transactions =
                scope.ServiceProvider.GetRequiredService<TransactionsDb>();

            // A is refunded already.
            Transaction refunded = await transactions.Transactions
                .SingleAsync(t => t.Id == refundedId, TestContext.Current.CancellationToken);
            refunded.MarkRefundPending(Money.Of(100m, Currency.KWD), RefundCause.GuestCancellation);

            await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Guid secondId;

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            TransactionsDb transactions =
                scope.ServiceProvider.GetRequiredService<TransactionsDb>();

            // B succeeded afterwards - the pair a SingleOrDefault query cannot read.
            Transaction second = Transaction.Create(Guid.CreateVersion7(), bookingId, Money.Of(200m, Currency.KWD));
            second.MarkSucceeded(DateTimeOffset.UtcNow);
            transactions.Transactions.Add(second);
            await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
            secondId = second.Id;
        }

        // Act - B's own confirmation, which names B by id.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await ResolveInScopeAsync(
                scope, reversal => reversal.RefundUnusablePaymentByTransactionAsync(secondId, TestContext.Current.CancellationToken));
        }

        using IServiceScope assertScope = factory.Services.CreateScope();
        TransactionsDb db = assertScope.ServiceProvider.GetRequiredService<TransactionsDb>();

        // B is refunded in full - it bought nothing, and the obligation was
        // already settled against A.
        Transaction resolvedSecond = await db.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == secondId, TestContext.Current.CancellationToken);
        Assert.Equal(TransactionStatus.RefundPending, resolvedSecond.TransactionStatus);
        Assert.Equal(200m, resolvedSecond.RefundAmount!.Value.Amount);

        // A is untouched - its own refund stands at the policy figure.
        Transaction untouched = await db.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == refundedId, TestContext.Current.CancellationToken);
        Assert.Equal(100m, untouched.RefundAmount!.Value.Amount);
        Assert.Equal(RefundCause.GuestCancellation, untouched.RefundCause);
    }

    [Fact]
    public async Task APaymentStateRead_DescribesOneMomentRatherThanTwo()
    {
        // 4b: describing a payment takes one read. As two - "is there a refund"
        // then "is anything owed" - a resolver can move the payment
        // Succeeded -> RefundPending in between; the first read sees no
        // refund, the second no succeeded payment, and the response reports
        // neither state.
        //
        // This pins the property directly: for a payment in the RefundPending
        // state, one observation reports the refund rather than "nothing to
        // refund".
        (Guid bookingId, Guid transactionId) = await SeedCancelledAndPaidAsync(daysUntilCheckIn: 165);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            TransactionsDb transactions =
                scope.ServiceProvider.GetRequiredService<TransactionsDb>();

            Transaction payment = await transactions.Transactions
                .SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);

            payment.MarkRefundPending(Money.Of(100m, Currency.KWD), RefundCause.GuestCancellation);
            await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using IServiceScope readScope = factory.Services.CreateScope();

        PaymentStateSnapshot? state = await readScope.ServiceProvider.GetRequiredService<IPaymentReversal>()
            .GetPaymentStateAsync(bookingId, TestContext.Current.CancellationToken);

        Assert.NotNull(state);

        // The refund is visible. A read asking only for Succeeded reports
        // nothing for this exact state.
        Assert.Equal(100m, state.RefundAmount!.Value.Amount);
        Assert.Equal(RefundStatus.Pending, state.RefundStatus);

        // And it is not simultaneously reported as still awaiting one, which is
        // the contradiction the two separate reads could produce.
        Assert.False(state.AwaitingRefund);
        Assert.Equal(200m, state.Amount.Amount);
    }

    [Fact]
    public async Task TwoResolversRacing_LeaveOneRefundAndOneSettledObligation()
    {
        // The already-finalized branch. Both read a Succeeded transaction, one
        // writes, and the loser's MarkRefundPending throws.
        //
        // The loser must still mark the obligation resolved. Returning without
        // marking would leave the sweep retrying indefinitely against a
        // transaction that will never be Succeeded again.
        (Guid bookingId, Guid transactionId) = await SeedCancelledAndPaidAsync(daysUntilCheckIn: 162);

        await Task.WhenAll(ResolveAsync(bookingId), ResolveAsync(bookingId));

        (Transaction payment, RefundObligation obligation) = await ReadAsync(bookingId, transactionId);

        Assert.Equal(TransactionStatus.RefundPending, payment.TransactionStatus);
        Assert.Equal(100m, payment.RefundAmount!.Value.Amount);
        Assert.NotNull(obligation.ResolvedAt);
    }
}
