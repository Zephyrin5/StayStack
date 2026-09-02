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
[Collection("Integration Tests")]
public class PricingRuleConstraintTests(IntegrationTestWebApplicationFactory factory)
{
    private static readonly DateOnly Anchor = new DateOnly(2027, 3, 1);

    private async Task<Guid> SeedUnitAsync()
    {
        Property property = CatalogSeeding.CreateProperty();
        Unit unit = CatalogSeeding.CreateUnit(property);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
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
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
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

        await AddRuleAsync(PricingRule.CreateDateRangeOverride(unitId, Anchor, Anchor.AddDays(10), 150m));

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddRuleAsync(PricingRule.CreateDateRangeOverride(unitId, Anchor.AddDays(5), Anchor.AddDays(15), 200m)));

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

        await AddRuleAsync(PricingRule.CreateDateRangeOverride(unitId, Anchor, Anchor.AddDays(10), 150m));
        await AddRuleAsync(PricingRule.CreateDateRangeOverride(unitId, Anchor.AddDays(10), Anchor.AddDays(20), 200m));
    }

    [Fact]
    public async Task DateRangeOverride_OverlappingRangeOnADifferentUnit_IsAccepted()
    {
        // unit_id WITH = is what scopes the constraint. Without it this would
        // be a platform-wide ban on two units sharing a promotional week.
        Guid firstUnitId = await SeedUnitAsync();
        Guid secondUnitId = await SeedUnitAsync();

        await AddRuleAsync(PricingRule.CreateDateRangeOverride(firstUnitId, Anchor, Anchor.AddDays(10), 150m));
        await AddRuleAsync(PricingRule.CreateDateRangeOverride(secondUnitId, Anchor, Anchor.AddDays(10), 200m));
    }

    [Fact]
    public async Task DateRangeOverride_OverlappingAnArchivedRule_IsAccepted()
    {
        // The WHERE clause carries status <> 2 for the same reason
        // ix_promotions_code does: an archived rule must not permanently
        // reserve its dates against the replacement that supersedes it.
        Guid unitId = await SeedUnitAsync();

        PricingRule archived = PricingRule.CreateDateRangeOverride(unitId, Anchor, Anchor.AddDays(10), 150m);
        archived.Archive(DateTimeOffset.UtcNow, null);
        await AddRuleAsync(archived);

        await AddRuleAsync(PricingRule.CreateDateRangeOverride(unitId, Anchor.AddDays(5), Anchor.AddDays(15), 200m));
    }

    [Fact]
    public async Task LengthOfStayDiscount_SecondActiveRuleForTheSameUnit_IsRejectedByTheDatabase()
    {
        // The one type where the read is ambiguous even without an "overlap"
        // in any geometric sense: PricingCalculator takes FirstOrDefault over
        // rules whose MinNights <= nights, so two active rules with different
        // MinNights would both match a long stay and the discount applied
        // would depend on row order.
        Guid unitId = await SeedUnitAsync();

        await AddRuleAsync(PricingRule.CreateLengthOfStayDiscount(unitId, minNights: 3, discountPercent: 10m));

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddRuleAsync(PricingRule.CreateLengthOfStayDiscount(unitId, minNights: 7, discountPercent: 20m)));

        PostgresException postgres = UnwrapPostgres(exception);
        Assert.Equal("23505", postgres.SqlState);
        Assert.Equal("ix_pricing_rules_unit_length_of_stay_active", postgres.ConstraintName);
    }

    [Fact]
    public async Task LengthOfStayDiscount_ReplacingAnArchivedRule_IsAccepted()
    {
        Guid unitId = await SeedUnitAsync();

        PricingRule archived = PricingRule.CreateLengthOfStayDiscount(unitId, minNights: 3, discountPercent: 10m);
        archived.Archive(DateTimeOffset.UtcNow, null);
        await AddRuleAsync(archived);

        await AddRuleAsync(PricingRule.CreateLengthOfStayDiscount(unitId, minNights: 7, discountPercent: 20m));
    }

    [Fact]
    public async Task DayOfWeekMultiplier_OverlappingDaysForTheSameUnit_IsStillOnlyGuardedByApplicationCode()
    {
        // Deliberately asserts the gap rather than hiding it. The third rule
        // type's invariant - no two active rules sharing a day - is array
        // overlap, and Postgres has no built-in GiST opclass for integer[],
        // so it cannot be an exclusion constraint without enabling the
        // intarray extension or normalising days into their own rows.
        //
        // So this write succeeds at the database. It is still rejected for any
        // caller going through CreatePricingRuleHandler, by
        // PricingRuleOverlapChecker under Serializable isolation - the same
        // protection the other two types had before this change, no weaker.
        // This test exists so that stays a known, deliberate asymmetry instead
        // of being rediscovered as a surprise.
        Guid unitId = await SeedUnitAsync();

        await AddRuleAsync(PricingRule.CreateDayOfWeekMultiplier(unitId, [5, 6], 1.5m));
        await AddRuleAsync(PricingRule.CreateDayOfWeekMultiplier(unitId, [6, 0], 1.25m));
    }
}
