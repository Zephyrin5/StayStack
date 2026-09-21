using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
namespace IntegrationTests.Features.Catalog;

// PricingCalculator resolves a nightly price with FirstOrDefault over an
// unordered ToListAsync result, so "at most one rule of a type matches" is a
// correctness precondition of the read path, not just a write-time nicety. A
// second matching rule would throw nowhere - it would silently make the price
// depend on row order.
//
// PricingRuleOverlapChecker enforces that in application code under
// Serializable isolation, which handles concurrent writers. It does not handle
// a writer that never calls it: a bulk import, a data migration, a future
// handler, raw SQL. These tests go around the checker deliberately - writing
// straight through the DbContext - so what they exercise is the schema.
//
// Reads SqlState and ConstraintName directly, which BannedSymbols.txt refuses elsewhere: the
// subject here is which constraint Postgres raised, and an exclusion violation and a unique-index
// violation are the two outcomes these tests have to tell apart. ConstraintViolations is the
// consumer of that fact, not a way to observe it.
#pragma warning disable RS0030
[Collection("Integration Tests")]
public class PricingRuleConstraintTests(IntegrationTestWebApplicationFactory factory)
{
    private static readonly DateOnly Anchor = new DateOnly(2027, 3, 1);

    private async Task<Guid> SeedUnitAsync()
    {
        Property property = CatalogSeeding.CreateProperty();
        Unit unit = CatalogSeeding.CreateUnit(property);

        using IServiceScope scope = factory.Services.CreateScope();
        CatalogDb context = scope.ServiceProvider.GetRequiredService<CatalogDb>();
        context.Add(property);
        context.Add(unit);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return unit.Id;
    }

    // Straight to SaveChanges, never through CreatePricingRuleHandler - the
    // point is what happens with no application-level check in the way.
    private async Task AddRuleAsync(PricingRule rule)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        CatalogDb context = scope.ServiceProvider.GetRequiredService<CatalogDb>();
        context.Add(rule);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static PostgresException UnwrapPostgres(DbUpdateException exception)
    {
        PostgresException? postgres = exception.InnerException as PostgresException;
        Assert.NotNull(postgres);
        return postgres;
    }

    [Fact]
    public async Task DateRangeOverride_OverlappingActiveRuleForTheSameUnit_IsRejectedByTheDatabase()
    {
        Guid unitId = await SeedUnitAsync();

        await AddRuleAsync(PricingRule.CreateDateRangeOverride(Guid.CreateVersion7(), unitId, Anchor, Anchor.AddDays(10), 150m));

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddRuleAsync(PricingRule.CreateDateRangeOverride(Guid.CreateVersion7(), unitId, Anchor.AddDays(5), Anchor.AddDays(15), 200m)));

        // 23P01 is exclusion_violation - the constraint, not a unique index or
        // an application check that happened to run anyway.
        Assert.Equal("23P01", UnwrapPostgres(exception).SqlState);
    }

    [Fact]
    public async Task DateRangeOverride_AdjacentRangesForTheSameUnit_AreAccepted()
    {
        // The constraint has to agree with PricingRuleOverlapChecker on
        // half-open semantics, or hosts lose a legal booking pattern: one rule
        // ending where the next begins shares no night and must be allowed.
        // Postgres daterange is [) natively, so && already means this - this
        // asserts it rather than assuming the two definitions coincide.
        Guid unitId = await SeedUnitAsync();

        await AddRuleAsync(PricingRule.CreateDateRangeOverride(Guid.CreateVersion7(), unitId, Anchor, Anchor.AddDays(10), 150m));
        await AddRuleAsync(PricingRule.CreateDateRangeOverride(Guid.CreateVersion7(), unitId, Anchor.AddDays(10), Anchor.AddDays(20), 200m));
    }

    [Fact]
    public async Task DateRangeOverride_OverlappingRangeOnADifferentUnit_IsAccepted()
    {
        // unit_id WITH = is what scopes the constraint. Without it this would
        // be a platform-wide ban on two units sharing a promotional week.
        Guid firstUnitId = await SeedUnitAsync();
        Guid secondUnitId = await SeedUnitAsync();

        await AddRuleAsync(PricingRule.CreateDateRangeOverride(Guid.CreateVersion7(), firstUnitId, Anchor, Anchor.AddDays(10), 150m));
        await AddRuleAsync(PricingRule.CreateDateRangeOverride(Guid.CreateVersion7(), secondUnitId, Anchor, Anchor.AddDays(10), 200m));
    }

    [Fact]
    public async Task DateRangeOverride_OverlappingAnArchivedRule_IsAccepted()
    {
        // The WHERE clause carries status <> 2 for the same reason
        // ix_promotions_code does: an archived rule must not permanently
        // reserve its dates against the replacement that supersedes it.
        Guid unitId = await SeedUnitAsync();

        PricingRule archived = PricingRule.CreateDateRangeOverride(Guid.CreateVersion7(), unitId, Anchor, Anchor.AddDays(10), 150m);
        archived.Archive(DateTimeOffset.UtcNow, null);
        await AddRuleAsync(archived);

        await AddRuleAsync(PricingRule.CreateDateRangeOverride(Guid.CreateVersion7(), unitId, Anchor.AddDays(5), Anchor.AddDays(15), 200m));
    }

    [Fact]
    public async Task LengthOfStayDiscount_ASecondRuleAtTheSameThreshold_IsRejectedByTheDatabase()
    {
        // Two tiers at one threshold are indistinguishable to PricingCalculator: it takes the
        // deepest MinNights a stay reaches, and with a tie there is nothing left to order them by,
        // so the discount would depend on row order. Different thresholds are ordered, and allowed.
        Guid unitId = await SeedUnitAsync();

        await AddRuleAsync(PricingRule.CreateLengthOfStayDiscount(Guid.CreateVersion7(), unitId, minNights: 7, discountPercent: 10m));

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddRuleAsync(PricingRule.CreateLengthOfStayDiscount(Guid.CreateVersion7(), unitId, minNights: 7, discountPercent: 20m)));

        PostgresException postgres = UnwrapPostgres(exception);
        Assert.Equal("23505", postgres.SqlState);
        Assert.Equal("ix_pricing_rules_unit_min_nights_active", postgres.ConstraintName);
    }

    [Fact]
    public async Task LengthOfStayDiscount_SeveralThresholdsForOneUnit_AreAccepted()
    {
        // The point of the change: a host can price a long weekend, a week and a month differently.
        Guid unitId = await SeedUnitAsync();

        await AddRuleAsync(PricingRule.CreateLengthOfStayDiscount(Guid.CreateVersion7(), unitId, minNights: 3, discountPercent: 5m));
        await AddRuleAsync(PricingRule.CreateLengthOfStayDiscount(Guid.CreateVersion7(), unitId, minNights: 7, discountPercent: 10m));
        await AddRuleAsync(PricingRule.CreateLengthOfStayDiscount(Guid.CreateVersion7(), unitId, minNights: 30, discountPercent: 25m));
    }

    [Fact]
    public async Task LengthOfStayDiscount_ReplacingAnArchivedRuleAtTheSameThreshold_IsAccepted()
    {
        // The index is partial on the soft-delete predicate, so retiring a tier and recreating it at
        // the same threshold works - the case a plain unique index would refuse forever.
        Guid unitId = await SeedUnitAsync();

        PricingRule archived = PricingRule.CreateLengthOfStayDiscount(Guid.CreateVersion7(), unitId, minNights: 7, discountPercent: 10m);
        archived.Archive(DateTimeOffset.UtcNow, null);
        await AddRuleAsync(archived);

        await AddRuleAsync(PricingRule.CreateLengthOfStayDiscount(Guid.CreateVersion7(), unitId, minNights: 7, discountPercent: 20m));
    }

    [Fact]
    public async Task DayOfWeekMultiplier_OverlappingDaysForTheSameUnit_IsRejectedByTheDatabase()
    {
        // Array overlap has no built-in GiST opclass, and intarray is not used.
        // The domain is seven weekdays, so one partial unique index per day holds
        // "each day in at most one active rule", and the violation names the day
        // that collided - Saturday here.
        Guid unitId = await SeedUnitAsync();

        await AddRuleAsync(PricingRule.CreateDayOfWeekMultiplier(Guid.CreateVersion7(), unitId, [5, 6], 1.5m));

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddRuleAsync(PricingRule.CreateDayOfWeekMultiplier(Guid.CreateVersion7(), unitId, [6, 0], 1.25m)));

        PostgresException postgres = UnwrapPostgres(exception);
        Assert.Equal("23505", postgres.SqlState);
        Assert.Equal("ix_pricing_rules_unit_day_of_week_6_active", postgres.ConstraintName);
    }

    [Fact]
    public async Task DayOfWeekMultiplier_DisjointDaysForTheSameUnit_AreAccepted()
    {
        // Per-day indexes must not overreach into "one multiplier per unit".
        Guid unitId = await SeedUnitAsync();

        await AddRuleAsync(PricingRule.CreateDayOfWeekMultiplier(Guid.CreateVersion7(), unitId, [5, 6], 1.5m));
        await AddRuleAsync(PricingRule.CreateDayOfWeekMultiplier(Guid.CreateVersion7(), unitId, [0, 1, 2], 0.9m));
    }

    [Fact]
    public async Task DayOfWeekMultiplier_ReplacingAnArchivedRule_IsAccepted()
    {
        Guid unitId = await SeedUnitAsync();

        PricingRule archived = PricingRule.CreateDayOfWeekMultiplier(Guid.CreateVersion7(), unitId, [5, 6], 1.5m);
        archived.Archive(DateTimeOffset.UtcNow, null);
        await AddRuleAsync(archived);

        await AddRuleAsync(PricingRule.CreateDayOfWeekMultiplier(Guid.CreateVersion7(), unitId, [6], 2m));
    }

    [Fact]
    public async Task DayOfWeekMultiplier_WithADayOutsideTheWeek_IsRejectedByTheDatabase()
    {
        // The per-day indexes only cover 0..6, so a 7 would sit outside all of
        // them and escape the invariant. PricingRule.ValidateDaysOfWeek refuses
        // it, which is exactly why this goes around the entity with raw SQL.
        Guid unitId = await SeedUnitAsync();

        using IServiceScope scope = factory.Services.CreateScope();
        CatalogDb context = scope.ServiceProvider.GetRequiredService<CatalogDb>();

        PostgresException postgres = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO catalog.pricing_rules (id, unit_id, rule_type, days_of_week, multiplier, status, created_at)
                VALUES ({Guid.CreateVersion7()}, {unitId}, 'DayOfWeekMultiplier', ARRAY[6, 7], 1.5, 0, now())
                """, TestContext.Current.CancellationToken));

        Assert.Equal("23514", postgres.SqlState);
        Assert.Equal("ck_pricing_rules_days_of_week_domain", postgres.ConstraintName);
    }

    [Fact]
    public async Task DateRangeOverride_InAThreeDecimalCurrency_KeepsItsThirdDecimalThroughStorage()
    {
        // 10.125 KWD is a valid amount, not a rounding artefact: KWD has
        // three minor-unit digits (CurrencyMinorUnits), and Money.Of rounds
        // to exactly that, so the domain produces this value and every other
        // monetary column stores it in numeric(12,3).
        //
        // override_price was numeric(10,2). Postgres narrows scale by
        // rounding rather than erroring, so this came back as 10.13 with
        // nothing raised anywhere - and the same unit's base price, mapped
        // through ConfigureMoney, kept all three digits. A host setting a
        // seasonal rate lost a fils per night and the only evidence was the
        // number itself.
        //
        // Through SaveChanges rather than the handler, like everything else
        // in this file: what is being tested is the column.
        Guid unitId = await SeedUnitAsync();

        PricingRule rule = PricingRule.CreateDateRangeOverride(Guid.CreateVersion7(), unitId, Anchor, Anchor.AddDays(3), 10.125m);
        await AddRuleAsync(rule);

        using IServiceScope scope = factory.Services.CreateScope();
        CatalogDb context = scope.ServiceProvider.GetRequiredService<CatalogDb>();
        PricingRule persisted = await context.PricingRules.AsNoTracking()
            .SingleAsync(r => r.Id == rule.Id, TestContext.Current.CancellationToken);

        Assert.Equal(10.125m, persisted.OverridePrice);
    }

}
#pragma warning restore RS0030
