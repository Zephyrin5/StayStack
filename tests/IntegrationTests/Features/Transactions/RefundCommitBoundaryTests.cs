// Proves refund resolution finishes from half-finished seeded states and from a status flip
// between the resolver's read and write; TwoResolversRacing fails 6/6 with the concurrency catch
// disabled. APaymentStateRead_DescribesOneMomentRatherThanTwo reads a static row and cannot tell
// one read from two.
using Bookings;
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
using Outbox;
using Transactions.Entities;
using Transactions.Outbox;
using Transactions.Serialization;
namespace IntegrationTests.Features.Transactions;

// The refund decision spans two databases and therefore two commits: the amount
// lands on the Transaction, and the obligation's ResolvedAt marker lands in
// Bookings. RefundDeterminismTests covers delivery *order* once both have been
// saved. None of it crosses a commit boundary, which is exactly why a marker
// that committed without its refund went unnoticed.
//
// Which write is inside a transaction depends on which dispatcher called the
// resolver, and the two are mirror images: OutboxDispatcherBase runs
// TryHandleAsync inside its claim transaction, so from Transactions' dispatcher
// the refund joins an uncommitted transaction while the Bookings marker
// autocommits, and from Bookings' dispatcher it is the reverse. Both directions
// are pinned here.
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
    ///     the state every test here starts from. Nothing is dispatched.
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

        booking.Cancel(cancelledAt);
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

        AppTransactionsDbContext transactions =
            scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

        Transaction payment = Transaction.Create(Guid.CreateVersion7(), bookingId, Money.Of(200m, Currency.KWD));
        payment.MarkSucceeded(succeededAt);
        transactions.Transactions.Add(payment);
        await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (bookingId, payment.Id);
    }

    private async Task ResolveAsync(Guid bookingId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITransactionReversal>()
            .ResolveRefundAsync(bookingId, TestContext.Current.CancellationToken);
    }

    private async Task<(Transaction Payment, RefundObligation Obligation)> ReadAsync(
        Guid bookingId, Guid transactionId)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        Transaction payment = await scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>()
            .Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);

        RefundObligation obligation = await scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>()
            .RefundObligations.AsNoTracking()
            .SingleAsync(o => o.BookingId == bookingId, TestContext.Current.CancellationToken);

        return (payment, obligation);
    }

    [Fact]
    public async Task AResolvedMarkerWithNoRefundBehindIt_DoesNotSuppressTheRefund()
    {
        // The reported defect. The marker commits on its own connection while
        // the refund is still inside the dispatcher's transaction; that
        // transaction then fails, so the refund rolls back and the marker
        // survives.
        //
        // Simulated by writing the marker directly, which is precisely the
        // state that rollback leaves behind - and cheaper than driving a
        // dispatcher failure to produce it.
        (Guid bookingId, Guid transactionId) = await SeedCancelledAndPaidAsync(daysUntilCheckIn: 160);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBookingLookup>()
                .MarkRefundObligationResolvedAsync(
                    bookingId, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        }

        // Act - the retry the dispatcher would perform.
        await ResolveAsync(bookingId);

        // Assert - the refund happened. Treating ResolvedAt as authority made
        // this return null, the message get marked processed, and the sweep skip
        // the row on ResolvedAt != null: lost permanently, with every mechanism
        // reporting success.
        (Transaction payment, RefundObligation obligation) = await ReadAsync(bookingId, transactionId);

        Assert.Equal(TransactionStatus.RefundPending, payment.TransactionStatus);
        Assert.Equal(100m, payment.RefundAmount!.Value.Amount);
        Assert.NotNull(obligation.ResolvedAt);
    }

    [Fact]
    public async Task ARefundWithNoMarkerBehindIt_FinishesTheBookkeeping()
    {
        // The mirror, from the other dispatcher. The refund commits and the
        // process dies before the marker.
        //
        // Querying for Succeeded alone meant every later run found nothing and
        // returned at the first step, so the obligation stayed unresolved
        // forever and the sweep re-processed it every cycle.
        (Guid bookingId, Guid transactionId) = await SeedCancelledAndPaidAsync(daysUntilCheckIn: 161);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppTransactionsDbContext transactions =
                scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

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
                AppTransactionsDbContext transactions =
                    scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

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
            Guid bookingId, DateTimeOffset resolvedAt, CancellationToken cancellationToken) =>
            inner.MarkRefundObligationResolvedAsync(bookingId, resolvedAt, cancellationToken);
    }

    [Fact]
    public async Task AResolverLosingTheRaceInsideADispatcher_StillMarksItsMessageProcessed()
    {
        // The previous race test calls the resolver directly, so it cannot see
        // this at all: the damage would be to an entity only the dispatcher is
        // tracking.
        //
        // TransactionsOutboxDispatcher loads its OutboxMessage tracked on the
        // scoped AppTransactionsDbContext and assigns ProcessedAt after the
        // handler returns. If the resolver's concurrency catch cleared that
        // context's change tracker, the message would be detached, ProcessedAt
        // would never be saved, and the dispatch would report success over a
        // message still pending - with nothing anywhere saying so.
        (Guid bookingId, Guid transactionId) = await SeedCancelledAndPaidAsync(daysUntilCheckIn: 163);

        Guid messageId = Guid.CreateVersion7();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppTransactionsDbContext transactions =
                scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

            transactions.Set<OutboxMessage>().Add(new OutboxMessage
            {
                Id = messageId,
                Type = ConfirmBookingPaymentOutboxMessage.TypeName,
                Payload = System.Text.Json.JsonSerializer.Serialize(
                    new ConfirmBookingPaymentOutboxMessage(transactionId, bookingId),
                    TransactionsJsonSerializerContext.Default.ConfirmBookingPaymentOutboxMessage),
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            });

            await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

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
            await hostScope.ServiceProvider.GetRequiredService<TransactionsOutboxDispatcher>()
                .DispatchPendingAsync(50, TestContext.Current.CancellationToken);
        }

        // From a fresh scope, because the whole defect is that the tracked
        // instance and the row disagreed.
        using IServiceScope assertScope = factory.Services.CreateScope();

        OutboxMessage persisted = await assertScope.ServiceProvider
            .GetRequiredService<AppTransactionsDbContext>()
            .Set<OutboxMessage>().AsNoTracking()
            .SingleAsync(m => m.Id == messageId, TestContext.Current.CancellationToken);

        Assert.NotNull(persisted.ProcessedAt);
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
            AppTransactionsDbContext transactions =
                scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

            // A is refunded already.
            Transaction refunded = await transactions.Transactions
                .SingleAsync(t => t.Id == refundedId, TestContext.Current.CancellationToken);
            refunded.MarkRefundPending(Money.Of(100m, Currency.KWD), RefundCause.GuestCancellation);

            await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Guid secondId;

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppTransactionsDbContext transactions =
                scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

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
            await scope.ServiceProvider.GetRequiredService<ITransactionReversal>()
                .RefundUnusablePaymentByTransactionAsync(secondId, TestContext.Current.CancellationToken);
        }

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppTransactionsDbContext db = assertScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

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
        // then "is anything owed" - a dispatcher or the sweep can move the
        // payment Succeeded -> RefundPending in between; the first read sees no
        // refund, the second no succeeded payment, and the response reports
        // neither state.
        //
        // This pins the property directly: for a payment in the RefundPending
        // state, one observation reports the refund rather than "nothing to
        // refund".
        (Guid bookingId, Guid transactionId) = await SeedCancelledAndPaidAsync(daysUntilCheckIn: 165);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppTransactionsDbContext transactions =
                scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

            Transaction payment = await transactions.Transactions
                .SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);

            payment.MarkRefundPending(Money.Of(100m, Currency.KWD), RefundCause.GuestCancellation);
            await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using IServiceScope readScope = factory.Services.CreateScope();

        PaymentStateSnapshot? state = await readScope.ServiceProvider.GetRequiredService<ITransactionReversal>()
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
