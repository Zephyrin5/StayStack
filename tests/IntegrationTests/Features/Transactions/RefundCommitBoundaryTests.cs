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
using Transactions.Entities;
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

        Transaction payment = Transaction.Create(bookingId, Money.Of(200m, Currency.KWD));
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

    [Fact]
    public async Task TwoResolversRacing_LeaveOneRefundAndOneSettledObligation()
    {
        // The already-finalized branch. Both read a Succeeded transaction, one
        // writes, and the loser's MarkRefundPending throws.
        //
        // It used to return without marking, so the obligation was left for the
        // sweep to retry indefinitely against a transaction that would never be
        // Succeeded again - the same permanent unresolved row as the mirror
        // case, reached from a race instead of a crash.
        (Guid bookingId, Guid transactionId) = await SeedCancelledAndPaidAsync(daysUntilCheckIn: 162);

        await Task.WhenAll(ResolveAsync(bookingId), ResolveAsync(bookingId));

        (Transaction payment, RefundObligation obligation) = await ReadAsync(bookingId, transactionId);

        Assert.Equal(TransactionStatus.RefundPending, payment.TransactionStatus);
        Assert.Equal(100m, payment.RefundAmount!.Value.Amount);
        Assert.NotNull(obligation.ResolvedAt);
    }
}
