using Bookings;
using Bookings.Entities;
using Dapper;
using Bookings.Contracts;
using Bookings.Jobs;
using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NpgsqlTypes;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using Bookings.Outbox;
using Promotions.Contracts;
using Transactions;
using Transactions.Contracts;
using Transactions.Entities;
namespace IntegrationTests.Features.Bookings;

// Confirming a checkout takes a unit off the market: the hold moves to
// 'pending_payment' and goes on blocking its range through the exclusion
// constraint, which has no status predicate. These pin the deadline that makes
// that claim finite and the job that enforces it (docs/adr/0020).
[Collection("Integration Tests")]
public class ExpireUnpaidBookingsTests(IntegrationTestWebApplicationFactory factory)
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

    private async Task<Guid> SeedHoldAsync(Guid unitId, DateOnly checkIn, DateOnly checkOut, string status)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        UnitAvailabilityHold hold = new UnitAvailabilityHold
        {
            Id = Guid.CreateVersion7(),
            UnitId = unitId,
            StayRange = new NpgsqlRange<DateOnly>(checkIn, true, checkOut, false),
            Status = status,
            GuestCount = 2,
            CreatedAt = DateTimeOffset.UtcNow,
            TotalPrice = Money.Of(100m, Currency.KWD),
            Subtotal = 100m
        };

        context.UnitAvailabilityHolds.Add(hold);
        await context.SaveChangesAsync();
        return hold.Id;
    }

    private async Task<Guid> SeedBookingAsync(Guid unitId, Guid holdId, DateTimeOffset paymentDueAt)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        Booking booking = Booking.Create(
            Guid.CreateVersion7(), unitId, holdId, null,
            "Jane Guest", "jane@example.com", null,
            DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(10)),
            DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(12)),
            2, Money.Of(200m, Currency.KWD), Money.Of(200m, Currency.KWD),
            CancellationPolicy.CreateDefault(), "Asia/Kuwait", paymentDueAt);

        context.Bookings.Add(booking);
        await context.SaveChangesAsync();
        return booking.Id;
    }

    private static ExpireUnpaidBookingsJob CreateJob(IServiceScope scope, TimeProvider timeProvider)
    {
        return new ExpireUnpaidBookingsJob(
            scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>(),
            scope.ServiceProvider.GetRequiredService<IHoldConfirmation>(),
            scope.ServiceProvider.GetRequiredService<ITransactionLookup>(),
            scope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>(),
            timeProvider,
            NullLogger<ExpireUnpaidBookingsJob>.Instance);
    }

    private static FakeTimeProvider At(DateTimeOffset instant)
    {
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(instant);
        return timeProvider;
    }

    // The same DbContext instance HoldConfirmation resolved from this scope -
    // an advisory transaction is only ambient to the context that opened it.
    private static AppBookingsDbContext ContextIn(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

    private async Task<string> GetHoldStatusAsync(Guid holdId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        UnitAvailabilityHold hold = await context.UnitAvailabilityHolds.AsNoTracking()
            .SingleAsync(h => h.Id == holdId);
        return hold.Status;
    }

    private async Task<BookingStatus> GetBookingStatusAsync(Guid bookingId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Booking booking = await context.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        return booking.BookingStatus;
    }

    [Fact]
    public async Task AnOverdueUnpaidBooking_IsCancelled_AndItsUnitReturnedToInventory()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(40));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");
        Guid bookingId = await SeedBookingAsync(unit.Id, holdId, now.AddMinutes(-1));

        using IServiceScope scope = factory.Services.CreateScope();
        ExpireUnpaidBookingsJob job = CreateJob(scope, At(now));

        // Act
        await job.ExpireAsync(null!, TestContext.Current.CancellationToken);

        // Assert - both halves, which is the whole reason this job lives in
        // Bookings rather than Availability. Releasing the hold alone would
        // leave the guest holding a booking with no inventory behind it;
        // cancelling alone would leave the range blocked forever.
        Assert.Equal(BookingStatus.Cancelled, await GetBookingStatusAsync(bookingId));

        // 'held' with its timer reset, not deleted - the ordinary expiry
        // sweep reclaims it from here.
        Assert.Equal("held", await GetHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task ABookingStillWithinItsPaymentWindow_IsLeftAlone()
    {
        // The other half: a job that expired everything would pass the test
        // above while breaking every checkout in progress.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(41));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");
        Guid bookingId = await SeedBookingAsync(unit.Id, holdId, now.AddMinutes(15));

        using IServiceScope scope = factory.Services.CreateScope();
        ExpireUnpaidBookingsJob job = CreateJob(scope, At(now));

        // Act
        await job.ExpireAsync(null!, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BookingStatus.Pending, await GetBookingStatusAsync(bookingId));
        Assert.Equal("pending_payment", await GetHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task APaidBooking_IsNeverExpired_EvenIfItsDeadlineWouldHavePassed()
    {
        // The dangerous direction. A payment that resolves near the deadline
        // leaves a Confirmed booking whose hold is 'booked'; expiring it
        // would take a paid stay away from a guest and hand the range to
        // somebody else. Both the status filter and the null PaymentDueAt
        // that Confirm() leaves behind have to hold for that not to happen.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(42));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");
        Guid bookingId = await SeedBookingAsync(unit.Id, holdId, now.AddMinutes(-5));

        using (IServiceScope paymentScope = factory.Services.CreateScope())
        {
            IBookingPaymentConfirmation paymentConfirmation =
                paymentScope.ServiceProvider.GetRequiredService<IBookingPaymentConfirmation>();
            Assert.True(await paymentConfirmation.ConfirmPaymentAsync(bookingId, TestContext.Current.CancellationToken));
        }

        // Paying moves the hold the rest of the way, which is what makes
        // 'booked' a reachable state rather than a dead one.
        Assert.Equal("booked", await GetHoldStatusAsync(holdId));

        using IServiceScope scope = factory.Services.CreateScope();
        ExpireUnpaidBookingsJob job = CreateJob(scope, At(now));

        // Act
        await job.ExpireAsync(null!, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BookingStatus.Confirmed, await GetBookingStatusAsync(bookingId));
        Assert.Equal("booked", await GetHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task APaymentThatSucceededButWhoseConfirmationIsStillOnTheOutbox_IsNotExpired()
    {
        // The failure this job could not see. Every other test here reaches
        // the job through Bookings state, which is exactly the blind spot:
        // MarkTransactionSucceededHandler commits Succeeded and its outbox row
        // together and then dispatches inline as a best effort. When that best
        // effort fails, the row waits for OutboxRelayJob's cron - and through
        // that whole window the booking is still Pending with a lapsed
        // PaymentDueAt, which is precisely what the scan looks for.
        //
        // The re-check under the row lock does not help. It re-reads the same
        // Bookings rows, and no Bookings row knows about the payment yet.
        //
        // What followed was not just a lost booking: the expiry cancels, the
        // confirmation finally arrives, ConfirmPaymentAsync sees Cancelled and
        // returns false, and TransactionsOutboxDispatcher refunds a payment
        // that succeeded minutes earlier.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(47));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");
        Guid bookingId = await SeedBookingAsync(unit.Id, holdId, now.AddMinutes(-1));

        // The payment, committed on the Transactions side with nothing
        // dispatched - the withheld delivery, modelled by simply not
        // delivering it. Going through MarkTransactionSucceededHandler would
        // dispatch inline and confirm the booking, which is the case the
        // existing "a paid booking is never expired" test already covers and a
        // different failure mode entirely.
        using (IServiceScope paymentScope = factory.Services.CreateScope())
        {
            AppTransactionsDbContext transactions =
                paymentScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

            Transaction payment = Transaction.Create(Guid.CreateVersion7(), bookingId, Money.Of(200m, Currency.KWD));
            payment.MarkSucceeded(DateTimeOffset.UtcNow);
            transactions.Transactions.Add(payment);
            await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The booking is untouched by that - which is the point.
        Assert.Equal(BookingStatus.Pending, await GetBookingStatusAsync(bookingId));

        using IServiceScope scope = factory.Services.CreateScope();
        ExpireUnpaidBookingsJob job = CreateJob(scope, At(now));

        // Act
        await job.ExpireAsync(null!, TestContext.Current.CancellationToken);

        // Assert - neither half of the expiry ran. Both matter: cancelling
        // loses the stay, and releasing the hold hands the range to somebody
        // else, which is what makes the loss unrecoverable.
        Assert.Equal(BookingStatus.Pending, await GetBookingStatusAsync(bookingId));
        Assert.Equal("pending_payment", await GetHoldStatusAsync(holdId));

        // And the delayed confirmation still lands correctly when it finally
        // does arrive. Without this the test would prove only that the booking
        // survived one sweep, not that the payment ends up buying the stay it
        // paid for - which is the outcome the guest cares about and the one
        // the refund path was destroying.
        using (IServiceScope confirmScope = factory.Services.CreateScope())
        {
            Assert.True(await confirmScope.ServiceProvider.GetRequiredService<IBookingPaymentConfirmation>()
                .ConfirmPaymentAsync(bookingId, TestContext.Current.CancellationToken));
        }

        Assert.Equal(BookingStatus.Confirmed, await GetBookingStatusAsync(bookingId));
        Assert.Equal("booked", await GetHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task ABookingWhosePaymentWasRefunded_IsExpirableAgain()
    {
        // The other edge of the same check, and the reason it tests Succeeded
        // rather than "has a transaction". A payment that was taken and then
        // given back leaves nothing outstanding, so the deadline applies again
        // - a check written as "any transaction exists" would block this
        // booking's inventory forever.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(48));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");
        Guid bookingId = await SeedBookingAsync(unit.Id, holdId, now.AddMinutes(-1));

        using (IServiceScope paymentScope = factory.Services.CreateScope())
        {
            AppTransactionsDbContext transactions =
                paymentScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();

            Transaction payment = Transaction.Create(Guid.CreateVersion7(), bookingId, Money.Of(200m, Currency.KWD));
            payment.MarkSucceeded(DateTimeOffset.UtcNow);
            payment.MarkRefundPending(Money.Of(200m, Currency.KWD), RefundCause.GuestCancellation);
            payment.MarkRefunded();
            transactions.Transactions.Add(payment);
            await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using IServiceScope scope = factory.Services.CreateScope();
        ExpireUnpaidBookingsJob job = CreateJob(scope, At(now));

        // Act
        await job.ExpireAsync(null!, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BookingStatus.Cancelled, await GetBookingStatusAsync(bookingId));
        Assert.Equal("held", await GetHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task ReleasingAClaimedHold_Works_WhichIsWhatEveryCompensationDependsOn()
    {
        // ReleaseHoldAsync must match 'pending_payment'. Matching 'booked'
        // alone would turn every compensating release (ConfirmBookingHandler's
        // failure paths, ReconcileOrphanedBookingIntentsJob,
        // CancelBookingHandler, this expiry job) into a silent zero-row no-op
        // that strands the hold. Nothing about that failure is loud.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(43));
        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");

        using IServiceScope scope = factory.Services.CreateScope();
        IHoldConfirmation holdConfirmation = scope.ServiceProvider.GetRequiredService<IHoldConfirmation>();

        // Act - in a transaction, because HoldConfirmation now requires one
        // for every status transition. See RequiredTransaction: the release is
        // always half of a decision whose other half is a Bookings row, and a
        // caller without a transaction is one that cannot keep them together.
        await using (IDbContextTransaction release = await ContextIn(scope)
                         .Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await holdConfirmation.ReleaseHoldAsync(holdId, TestContext.Current.CancellationToken);
            await release.CommitAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Equal("held", await GetHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task APaymentResolvingWhileExpiryHoldsTheRowLock_DoesNotOverwriteTheCancellation()
    {
        // The race the sequential payment/expiry tests cannot reach, because
        // they let one finish before starting the other.
        //
        // Booking carries no concurrency token, so an EF update keyed on Id
        // cannot fail. If payment read without the row lock, the interleaving
        // would be: payment reads Pending, expiry commits Cancelled and releases
        // the hold, payment's UPDATE puts Confirmed back - a Confirmed booking
        // whose inventory was just handed to somebody else.
        //
        // Driven from the test rather than by racing the real job, so the
        // interleaving is deterministic: this transaction does exactly what
        // ExpireUnpaidBookingsJob does - lock the row, release the hold,
        // cancel the booking - and holds the lock open until payment is
        // provably blocked on it.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(44));
        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");
        Guid bookingId = await SeedBookingAsync(unit.Id, holdId, DateTimeOffset.UtcNow.AddMinutes(-1));

        using IServiceScope expiryScope = factory.Services.CreateScope();
        AppBookingsDbContext expiryContext = expiryScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        await using IDbContextTransaction expiryTransaction =
            await expiryContext.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        DbConnection expiryConnection = expiryContext.Database.GetDbConnection();
        await expiryConnection.ExecuteAsync(new CommandDefinition(
            """SELECT id FROM "bookings" WHERE id = @BookingId FOR UPDATE""",
            new { BookingId = bookingId },
            expiryTransaction.GetDbTransaction(),
            cancellationToken: TestContext.Current.CancellationToken));

        // Act - payment starts while the row is locked and the hold is still
        // claimable, so it gets past MarkHoldPaidAsync and then blocks on the
        // booking's row lock. That is the boundary under test: the ordering
        // fix alone would not save this case, because the hold transition
        // succeeds and the booking write is still to come.
        Task<bool> payment = Task.Run(async () =>
        {
            using IServiceScope paymentScope = factory.Services.CreateScope();
            return await paymentScope.ServiceProvider.GetRequiredService<IBookingPaymentConfirmation>()
                .ConfirmPaymentAsync(bookingId, CancellationToken.None);
        });

        // Long enough to have reached the lock and be waiting there. Without
        // the lock - the original behaviour - it would sail straight past,
        // read the still-Pending row, and be poised to overwrite whatever
        // this transaction commits.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(payment.IsCompleted);

        // Now expiry finishes its work while still holding the lock, exactly
        // as the job does: hand the hold back, then cancel the booking.
        // Already inside expiryTransaction on this same context, which is what
        // HoldConfirmation requires - and what the job it stands in for does.
        await expiryScope.ServiceProvider.GetRequiredService<IHoldConfirmation>()
            .ReleaseHoldAsync(holdId, TestContext.Current.CancellationToken);

        // Raw SQL rather than EF for the cancellation: this transaction is
        // started by hand, and EF refuses to run a query inside one it did
        // not create while a retrying execution strategy is configured.
        // Dapper on the same connection and transaction reproduces the job's
        // write exactly, which is all this needs.
        await expiryConnection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE "bookings" SET booking_status = 'Cancelled', payment_due_at = NULL
            WHERE id = @BookingId
            """,
            new { BookingId = bookingId },
            expiryTransaction.GetDbTransaction(),
            cancellationToken: TestContext.Current.CancellationToken));

        await expiryTransaction.CommitAsync(TestContext.Current.CancellationToken);

        bool confirmed = await payment;

        // Assert - payment observed the committed cancellation and reported
        // it, which is what routes the caller to a refund.
        Assert.False(confirmed);

        // And it did not overwrite the outcome. This is the assertion that
        // fails against the unlocked version.
        Assert.Equal(BookingStatus.Cancelled, await GetBookingStatusAsync(bookingId));
    }

    [Fact]
    public async Task MarkingAHoldPaidTwice_Succeeds_BecauseARetryMustBeAbleToFinishTheJob()
    {
        // Payment marks the hold paid and then confirms the booking, on two
        // DbContexts against two schemas. A crash between them, or a retried
        // outbox message, replays the first half against a hold that is
        // already 'booked'. If that were rejected, the retry meant to finish
        // the job would be the thing that permanently failed it - and the
        // booking would never leave Pending.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(45));
        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");

        using IServiceScope scope = factory.Services.CreateScope();
        IHoldConfirmation holdConfirmation = scope.ServiceProvider.GetRequiredService<IHoldConfirmation>();

        // Act - one transaction per call, mirroring the two separate attempts
        // this is standing in for. Both are transitions, so both need one.
        bool first;
        bool second;

        await using (IDbContextTransaction attempt = await ContextIn(scope)
                         .Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            first = await holdConfirmation.MarkHoldPaidAsync(holdId, TestContext.Current.CancellationToken);
            await attempt.CommitAsync(TestContext.Current.CancellationToken);
        }

        await using (IDbContextTransaction retry = await ContextIn(scope)
                         .Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            second = await holdConfirmation.MarkHoldPaidAsync(holdId, TestContext.Current.CancellationToken);
            await retry.CommitAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.True(first);
        Assert.True(second);
        Assert.Equal("booked", await GetHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task MarkingAReleasedHoldPaid_Fails_SoTheCallerCompensatesInsteadOfSellingNothing()
    {
        // The distinction the idempotency above must not swallow: a hold
        // released or expired out from under a late-landing payment is
        // inventory this platform no longer owns, and reporting success would
        // confirm a stay it cannot honour.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(46));
        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "held");

        using IServiceScope scope = factory.Services.CreateScope();
        IHoldConfirmation holdConfirmation = scope.ServiceProvider.GetRequiredService<IHoldConfirmation>();

        // Act
        bool marked;

        await using (IDbContextTransaction attempt = await ContextIn(scope)
                         .Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            marked = await holdConfirmation.MarkHoldPaidAsync(holdId, TestContext.Current.CancellationToken);
            await attempt.CommitAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.False(marked);
        Assert.Equal("held", await GetHoldStatusAsync(holdId));
    }

}
