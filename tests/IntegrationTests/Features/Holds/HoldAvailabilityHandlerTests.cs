using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Jobs;
using Bookings.Exceptions;
using Bookings.Features.HoldAvailability;
using BuildingBlocks.Exceptions;
using Catalog;
using Catalog.Contracts;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NpgsqlTypes;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace IntegrationTests.Features.Holds;

[Collection("Integration Tests")]
public class HoldAvailabilityHandlerTests(IntegrationTestWebApplicationFactory factory)
{
    // Properties for units built by CreateTestUnit below, flushed by the
    // seeder so a unit is never persisted without its owner - see
    // CatalogSeeding.
    private readonly List<Property> _pendingProperties = [];

    private async Task SeedCatalogAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        // Owners first - a Unit without its Property no longer resolves.
        context.AddRange(_pendingProperties);
        _pendingProperties.Clear();

        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private async Task SeedAvailabilityAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    /// <summary>
    ///     A fixed instant, deliberately a month ahead of the real clock, for
    ///     the two tests whose <c>'held'</c> rows have to still be there when
    ///     the assertion runs.
    ///     <para>
    ///         Everything else in this file pins <c>2026-08-20</c>, and should:
    ///         two tests depend on that date being a Thursday (day-of-week
    ///         pricing) or on 21:30 UTC being tomorrow in Kuwait, and the rest
    ///         either assert on the handler's return value or seed rows with a
    ///         null <c>hold_expires_at</c>, which no sweep predicate matches.
    ///     </para>
    ///     <para>
    ///         <b>These two are different, and the reason is a job rather than
    ///         a date.</b> The test host runs TickerQ on the real clock, and
    ///         ExpiredHoldsSweepJob deletes <em>every</em> row matching
    ///         <c>status = 'held' AND hold_expires_at &lt;= now()</c>
    ///         platform-wide, every five minutes. A hold minted at a fake
    ///         2026-08-20 gets <c>hold_expires_at</c> fifteen minutes later -
    ///         so once the real date passed it, every such row was born already
    ///         eligible for that sweep. The two tests and the scheduler
    ///         disagreed about what "expired" meant, and they broke in opposite
    ///         directions:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <c>Handle_SixthActiveHoldFromSameClientNetwork...</c>
    ///                 fills the cap with five live holds and expects the sixth
    ///                 to be refused. A sweep landing between the fifth and the
    ///                 sixth deleted all five and the sixth succeeded - the
    ///                 intermittent failure this fixes.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>Handle_WithExpiredHeldRowsOnDifferentUnits...</c> is
    ///                 the worse one, because it was <em>green</em>. It seeds
    ///                 five expired rows to prove the cap's count query excludes
    ///                 them; if the sweep gets there first there is nothing left
    ///                 to exclude, and it passes without exercising the
    ///                 predicate it exists for.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         Dating forward separates the two clocks. A row these tests call
    ///         expired (<c>fixedInstant - 1 min</c>) is still an hour in the
    ///         future by <c>now()</c>, so the sweep leaves it alone and the
    ///         count query has to do its own job. Every date in both tests is
    ///         derived from this instant, so no fake-clock relationship
    ///         changes - only the real-clock one, which was the bug.
    ///     </para>
    /// </summary>
    private static DateTimeOffset InstantOutliving(TimeSpan sweepMargin) =>
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero) + sweepMargin + TimeSpan.FromHours(12);

    /// <summary>
    ///     Runs ExpiredHoldsSweepJob on the real clock, which is how TickerQ
    ///     runs it. Called at the exact moment the scheduler's own timing made
    ///     dangerous, so the hazard is exercised on every run instead of once
    ///     in a while.
    /// </summary>
    private static Task SweepExpiredHoldsAsync(IServiceScope scope) =>
        new ExpiredHoldsSweepJob(
                scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>(),
                TimeProvider.System)
            .SweepAsync(null!, TestContext.Current.CancellationToken);

    private Unit CreateTestUnit(int maxCapacity = 2)
    {
        // Built on a real Property, not a throwaway id - see CatalogSeeding.
        Property property = CatalogSeeding.CreateProperty();
        _pendingProperties.Add(property);

        return Unit.Create(
            Guid.CreateVersion7(),
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            maxCapacity,
            100);
    }

    // Real IUnitLookup, not a mock - these tests verify actual pricing math
    // (PricingCalculator via Catalog's own database), which a mock would
    // defeat the purpose of. Resolved once per handler construction, same
    // as the real AppBookingsDbContext.
    //
    // The cap defaults low here rather than to production's 25 so the cap
    // tests stay short. That's safe only because every request in this file
    // carries its own ClientKey - the count is scoped to one key, so holds
    // left behind by other tests sharing this database can't push a
    // neighbouring test over the limit.
    private HoldAvailabilityHandler CreateHandler(
        AppBookingsDbContext context, TimeProvider timeProvider, IServiceScope scope, int maxActiveHoldsPerClient = 5)
    {
        IUnitLookup unitLookup = scope.ServiceProvider.GetRequiredService<IUnitLookup>();
        // Resolved rather than constructed, so these tests exercise the same
        // lead-time bound the search path reads - see StaySearchPolicyOptions.
        IOptions<StaySearchPolicyOptions> staySearchPolicy =
            scope.ServiceProvider.GetRequiredService<IOptions<StaySearchPolicyOptions>>();
        return new HoldAvailabilityHandler(
            context, unitLookup, timeProvider,
            Options.Create(new HoldCapOptions { MaxActiveHoldsPerClient = maxActiveHoldsPerClient }),
            staySearchPolicy);
    }

    [Fact]
    public async Task Handle_ValidRequest_CreatesHoldAndPersistsToDatabase()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(3),
            GuestCount = 2,
            ClientKey = Guid.NewGuid().ToString()
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.HoldId);

        UnitAvailabilityHold? persistedHold = await context.UnitAvailabilityHolds
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == result.HoldId);

        Assert.NotNull(persistedHold);
        Assert.Equal(unit.Id, persistedHold.UnitId);
        Assert.Equal(new NpgsqlRange<DateOnly>(today, true,
            today.AddDays(3), false), persistedHold.StayRange);
        Assert.Equal(2, persistedHold.GuestCount);

        // Price/currency snapshotted at hold time (100/night * 3 nights),
        // not left to be recomputed from a possibly-changed unit price
        // later at confirm time.
        Assert.Equal(300m, persistedHold.TotalPrice.Amount);
        Assert.Equal(SeedWork.Enums.Currency.KWD, persistedHold.TotalPrice.Currency);

        // TimeProvider-derived, not DateTime.UtcNow - deterministic given
        // the FakeTimeProvider above.
        Assert.Equal(fixedInstant.AddMinutes(15).UtcDateTime, result.HoldExpiresAt);
    }

    [Fact]
    public async Task Handle_AdjacentDateRanges_SucceedsWithoutExclusionViolation()
    {
        // Verifies half-open interval [) logic: CheckOut of Hold A == CheckIn of Hold B
        // Arrange
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        UnitAvailabilityHold existingHold = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = unit.Id,
            StayRange = new NpgsqlRange<DateOnly>(today, true, today.AddDays(2), false), // Aug 20 -> Aug 22
            TotalPrice = Money.Of(100m, Currency.KWD),
            Subtotal = 100m
        };

        await SeedCatalogAsync(unit);
        await SeedAvailabilityAsync(existingHold);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today.AddDays(2), // Aug 22 (Same day previous hold ends)
            CheckOut = today.AddDays(4), // Aug 24
            GuestCount = 2,
            ClientKey = Guid.NewGuid().ToString()
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.HoldId);
    }

    [Fact]
    public async Task Handle_ExpiredHeldRowOverlapsRequestedRange_DeletesStaleHoldAndSucceeds()
    {
        // A held row nobody ever confirmed or retried sits in 'held' past
        // its hold_expires_at otherwise forever - the exclusion constraint
        // has no WHERE clause of its own to ignore it, so the handler must
        // actively delete it before inserting. Proves that cleanup fires.
        // Arrange
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        UnitAvailabilityHold expiredHold = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = unit.Id,
            Status = "held",
            StayRange = new NpgsqlRange<DateOnly>(today, true, today.AddDays(2), false),
            HoldExpiresAt = fixedInstant.AddMinutes(-1),
            CreatedAt = fixedInstant.AddMinutes(-16),
            TotalPrice = Money.Of(100m, Currency.KWD),
            Subtotal = 100m
        };

        await SeedCatalogAsync(unit);
        await SeedAvailabilityAsync(expiredHold);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(2),
            GuestCount = 2,
            ClientKey = Guid.NewGuid().ToString()
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotEqual(Guid.Empty, result.HoldId);

        List<UnitAvailabilityHold> holds = await context.UnitAvailabilityHolds
            .AsNoTracking()
            .Where(h => h.UnitId == unit.Id)
            .ToListAsync();

        // The stale row is gone, not just superseded - only the new hold remains.
        UnitAvailabilityHold onlyHold = Assert.Single(holds);
        Assert.Equal(result.HoldId, onlyHold.Id);
    }

    [Fact]
    public async Task Handle_PostgresExclusionViolation_ThrowsUnitUnavailableException()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        UnitAvailabilityHold existingHold = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = unit.Id,
            StayRange = new NpgsqlRange<DateOnly>(today, today.AddDays(2)),
            TotalPrice = Money.Of(100m, Currency.KWD),
            Subtotal = 100m
        };

        await SeedCatalogAsync(unit);
        await SeedAvailabilityAsync(existingHold);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(2),
            GuestCount = 2,
            ClientKey = Guid.NewGuid().ToString()
        };

        // Act & Assert
        await Assert.ThrowsAsync<UnitUnavailableException>(() =>
            handler.Handle(command, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_ActiveDateRangeOverride_ChargesOverridePriceForCoveredNights()
    {
        // Arrange - 3-night stay, the middle night is date-range overridden.
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        PricingRule overrideRule = PricingRule.CreateDateRangeOverride(Guid.CreateVersion7(),
            unit.Id, today.AddDays(1), today.AddDays(2), 500m);

        await SeedCatalogAsync(unit, overrideRule);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(3),
            GuestCount = 2,
            ClientKey = Guid.NewGuid().ToString()
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert - 100 (day 1) + 500 (overridden day 2) + 100 (day 3) = 700
        Assert.Equal(700m, result.TotalPrice);
    }

    [Fact]
    public async Task Handle_ActiveDayOfWeekMultiplier_ChargesMultipliedPriceForMatchingNights()
    {
        // Arrange - Aug 20 2026 is a Thursday; Aug 21 is a Friday.
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        PricingRule multiplierRule = PricingRule.CreateDayOfWeekMultiplier(Guid.CreateVersion7(), unit.Id, [(int)DayOfWeek.Friday], 2m);

        await SeedCatalogAsync(unit, multiplierRule);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(2), // Thu, Fri
            GuestCount = 2,
            ClientKey = Guid.NewGuid().ToString()
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert - 100 (Thu) + 200 (Fri, multiplied) = 300
        Assert.Equal(300m, result.TotalPrice);
    }

    [Fact]
    public async Task Handle_ActiveLengthOfStayDiscount_AppliesDiscountToSubtotal_WhenThresholdMet()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        PricingRule discountRule = PricingRule.CreateLengthOfStayDiscount(Guid.CreateVersion7(), unit.Id, 7, 10m);

        await SeedCatalogAsync(unit, discountRule);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(7),
            GuestCount = 2,
            ClientKey = Guid.NewGuid().ToString()
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert - 700 subtotal * 0.9 = 630
        Assert.Equal(630m, result.TotalPrice);
    }

    [Fact]
    public async Task Handle_UnitDoesNotExist_ThrowsNotFoundException()
    {
        // Arrange
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = Guid.NewGuid(), // Non-existent UnitId
            CheckIn = today,
            CheckOut = today.AddDays(2),
            GuestCount = 1,
            ClientKey = Guid.NewGuid().ToString()
        };

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.Handle(command, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_CheckInBeyondMaxLeadTime_ThrowsValidationException()
    {
        // Without this, an anonymous caller could hold a unit for [today,
        // today+3650) and the exclusion constraint would faithfully enforce
        // that decade-long block - see HoldAvailabilityHandler's own
        // lead-time bound, now StaySearchPolicyOptions.MaxLeadTimeDays.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today.AddDays(731),
            CheckOut = today.AddDays(733),
            GuestCount = 1,
            ClientKey = Guid.NewGuid().ToString()
        };

        // ValidationException, not ArgumentException: these are caller-input
        // rejections, and asserting the concrete type is what keeps them from
        // drifting back onto a BCL exception whose message the client would
        // then see decorated with an internal parameter name.
        ValidationException exception = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.Handle(command, CancellationToken.None).AsTask());
        Assert.Equal(nameof(HoldAvailabilityRequest.CheckIn), Assert.Single(exception.Errors).Key);
    }

    [Fact]
    public async Task Handle_SixthActiveHoldFromSameClientNetwork_ThrowsTooManyActiveHoldsException()
    {
        Unit unit = CreateTestUnit(maxCapacity: 10);
        await SeedCatalogAsync(unit);

        DateTimeOffset fixedInstant = InstantOutliving(sweepMargin: TimeSpan.FromDays(30));
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);
        string holderToken = Guid.NewGuid().ToString();
        string clientKey = Guid.NewGuid().ToString();

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        // Five non-overlapping ranges on the same unit, same client key -
        // all should succeed, filling the cap exactly.
        for (int i = 0; i < 5; i++)
        {
            HoldAvailabilityRequest request = new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = today.AddDays(i * 3),
                CheckOut = today.AddDays(i * 3 + 2),
                GuestCount = 1,
                ClientKey = clientKey
            };
            await handler.Handle(request, CancellationToken.None);
        }

        // The sweep, here, on purpose. TickerQ runs it every five minutes on
        // the real clock against every 'held' row on the platform, so it can
        // land in exactly this gap - and when these holds were minted at a
        // hardcoded 2026-08-20 they were born expired by that clock, so it
        // deleted all five and the sixth request sailed through. Running it
        // explicitly turns an intermittent failure into a permanent one if the
        // dating above is ever reverted.
        await SweepExpiredHoldsAsync(scope);

        // A 6th, on a range that doesn't even overlap the first five -
        // the cap is per-client, not per-unit/range, so a clean exclusion-
        // constraint check would otherwise let this through.
        HoldAvailabilityRequest sixthRequest = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today.AddDays(100),
            CheckOut = today.AddDays(102),
            GuestCount = 1,
            ClientKey = clientKey
        };

        await Assert.ThrowsAsync<TooManyActiveHoldsException>(() =>
            handler.Handle(sixthRequest, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_AfterFiveSuccessfulBookings_SixthHoldStillSucceeds()
    {
        // Regression test: the active-hold count previously included
        // 'booked' holds, which never revert to 'held' for a completed
        // booking (ConfirmHoldAsync sets 'booked' and only an explicit
        // release ever clears it, so the row persists for the life of the
        // booking). That meant a real customer would be locked out of
        // new holds after their 5th completed booking. 'booked' rows must
        // never count toward the cap.
        Unit unit = CreateTestUnit(maxCapacity: 10);
        await SeedCatalogAsync(unit);

        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);
        string holderToken = Guid.NewGuid().ToString();
        string clientKey = Guid.NewGuid().ToString();

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        // Five already-'booked' holds under the same client key,
        // simulating five completed bookings - none of these are "active"
        // in any meaningful sense; the customer already has real bookings,
        // not open holds.
        for (int i = 0; i < 5; i++)
        {
            context.Add(new UnitAvailabilityHold
            {
                Id = Guid.NewGuid(),
                UnitId = unit.Id,
                Status = "booked",
                StayRange = new NpgsqlRange<DateOnly>(today.AddDays(i * 3), true, today.AddDays(i * 3 + 2), false),
                BookedAt = fixedInstant,
                ClientKey = clientKey,
                TotalPrice = Money.Of(200m, Currency.KWD),
                Subtotal = 200m
            });
        }

        await context.SaveChangesAsync();

        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest sixthRequest = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today.AddDays(100),
            CheckOut = today.AddDays(102),
            GuestCount = 1,
            ClientKey = clientKey
        };

        HoldAvailabilityResponse result = await handler.Handle(sixthRequest, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.HoldId);
    }

    [Fact]
    public async Task Handle_AlternatingHoldAndConfirmWithoutPaying_StillHitsTheCap()
    {
        // The cap's escape hatch. It used to count 'held' only, so submitting
        // the checkout form - which moves a row to 'pending_payment' without
        // any money changing hands - took that row out of the count while the
        // exclusion constraint went on blocking its range. Hold, confirm,
        // repeat: a caller with no account and no card accumulated blocked
        // ranges without limit, and the per-client cap, the only concurrency
        // bound on this, never fired.
        //
        // Alternating rather than seeding five 'pending_payment' rows
        // directly: the seeded version passes against a cap that counts the
        // status but still lets the transition itself free a slot, which is
        // the actual bug. The loop below is the exploit, written out.
        Unit unit = CreateTestUnit(maxCapacity: 10);
        await SeedCatalogAsync(unit);

        // The real clock, unlike the sibling cap tests. IHoldConfirmation is
        // internal to Bookings and only reachable through the container, so it
        // gets the container's real TimeProvider - and a hold minted at a
        // fixed 2026 instant reads as long expired to it, failing
        // ConfirmHoldAsync's hold_expires_at > @Now guard before the cap is
        // ever exercised. Both clocks have to agree for the alternation to be
        // the thing under test.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
        string clientKey = Guid.NewGuid().ToString();

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        // Resolved from the container so this exercises the same transition
        // ConfirmBookingHandler performs, not a hand-written UPDATE that
        // could drift from it.
        IHoldConfirmation holdConfirmation = scope.ServiceProvider.GetRequiredService<IHoldConfirmation>();

        for (int i = 0; i < 5; i++)
        {
            HoldAvailabilityResponse held = await handler.Handle(new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = today.AddDays(i * 3),
                CheckOut = today.AddDays(i * 3 + 2),
                GuestCount = 1,
                ClientKey = clientKey
            }, CancellationToken.None);

            // Checkout submitted, nothing paid. Under the old cap this line
            // is what made the loop unbounded.
            //
            // In a transaction because HoldConfirmation now insists on one for
            // every status transition: each is half of a decision whose other
            // half is a Bookings row, and this test is standing in for the
            // handler that would own both.
            await using (IDbContextTransaction checkout =
                         await context.Database.BeginTransactionAsync(CancellationToken.None))
            {
                await holdConfirmation.ConfirmHoldAsync(held.HoldId, CancellationToken.None);
                await checkout.CommitAsync(CancellationToken.None);
            }
        }

        HoldAvailabilityRequest sixthRequest = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today.AddDays(100),
            CheckOut = today.AddDays(102),
            GuestCount = 1,
            ClientKey = clientKey
        };

        await Assert.ThrowsAsync<TooManyActiveHoldsException>(() =>
            handler.Handle(sixthRequest, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_WithExpiredHeldRowsOnDifferentUnits_DoesNotCountThemTowardTheCap()
    {
        // Regression test: the inline per-unit cleanup DELETE only touches
        // the unit being held right now, so an expired 'held' row on a
        // different unit survives until ExpiredHoldsSweepJob reaps it (up
        // to 5 minutes later). The active-hold count must exclude expired
        // rows itself, otherwise a guest who abandons checkout on five
        // different units is locked out of a sixth with zero live holds.
        Unit unit = CreateTestUnit(maxCapacity: 10);
        await SeedCatalogAsync(unit);

        DateTimeOffset fixedInstant = InstantOutliving(sweepMargin: TimeSpan.FromDays(30));
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);
        string holderToken = Guid.NewGuid().ToString();
        string clientKey = Guid.NewGuid().ToString();

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        for (int i = 0; i < 5; i++)
        {
            context.Add(new UnitAvailabilityHold
            {
                Id = Guid.NewGuid(),
                UnitId = Guid.NewGuid(), // a different unit each time
                Status = "held",
                StayRange = new NpgsqlRange<DateOnly>(today, true, today.AddDays(2), false),
                HoldExpiresAt = fixedInstant.AddMinutes(-1), // already expired
                ClientKey = clientKey,
                TotalPrice = Money.Of(100m, Currency.KWD),
                Subtotal = 100m
            });
        }

        await context.SaveChangesAsync();

        // Expired by this test's clock, and the sweep must still not be the
        // thing that removes them - otherwise there is nothing left for the
        // count query to exclude and this test proves nothing at all. It was
        // green for that reason before the instant above moved forward.
        await SweepExpiredHoldsAsync(scope);

        Assert.Equal(5, await context.UnitAvailabilityHolds.AsNoTracking()
            .CountAsync(h => h.ClientKey == clientKey, TestContext.Current.CancellationToken));

        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest sixthRequest = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(2),
            GuestCount = 1,
            ClientKey = clientKey
        };

        // Succeeds despite five rows still sitting there under the same key,
        // which is the whole point: the count query excludes them by date.
        HoldAvailabilityResponse result = await handler.Handle(sixthRequest, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.HoldId);
    }

    [Fact]
    public async Task Handle_CheckInIsYesterdayAtTheProperty_ButTodayInUtc_IsRejected()
    {
        // The behavioural proof for docs/adr/0018, and it fails under the old
        // UTC logic.
        //
        // 21:30 UTC on 2026-08-20 is already 00:30 on the 21st in
        // Asia/Kuwait, where this property is. So 2026-08-20 is *yesterday*
        // for the hotel and must not be bookable - but a UTC-derived "today"
        // reads 2026-08-20 and lets it through. That is the permissive
        // direction this app's own market sits on: holding a unit for a date
        // that has already passed locally.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateTimeOffset lateEveningUtc = new DateTimeOffset(2026, 8, 20, 21, 30, 0, TimeSpan.Zero);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(lateEveningUtc);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = new DateOnly(2026, 8, 20),
            CheckOut = new DateOnly(2026, 8, 23),
            GuestCount = 2,
            ClientKey = Guid.NewGuid().ToString()
        };

        // ValidationException, not ArgumentException: these are caller-input
        // rejections, and asserting the concrete type is what keeps them from
        // drifting back onto a BCL exception whose message the client would
        // then see decorated with an internal parameter name.
        ValidationException exception = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.Handle(command, CancellationToken.None).AsTask());
        Assert.Equal(nameof(HoldAvailabilityRequest.CheckIn), Assert.Single(exception.Errors).Key);
    }

    [Fact]
    public async Task Handle_CheckInIsTodayAtTheProperty_IsAccepted()
    {
        // The other half of the same boundary: the property's actual today
        // (the 21st at that instant) still holds normally, so the guard moved
        // rather than simply tightened.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateTimeOffset lateEveningUtc = new DateTimeOffset(2026, 8, 20, 21, 30, 0, TimeSpan.Zero);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(lateEveningUtc);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityResponse result = await handler.Handle(new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = new DateOnly(2026, 8, 21),
            CheckOut = new DateOnly(2026, 8, 24),
            GuestCount = 2,
            ClientKey = Guid.NewGuid().ToString()
        }, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.HoldId);
    }
}
