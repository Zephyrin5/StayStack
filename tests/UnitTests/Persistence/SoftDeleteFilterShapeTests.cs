using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Persistence;
using SeedWork.Abstractions;
using SeedWork.Enums;
using System.Linq.Expressions;
namespace UnitTests.Persistence;

// A change-detector, deliberately, and the only kind of test that can catch
// what it is watching for.
//
// ApplySoftDeleteQueryFilter excludes archived rows from every EF query. A
// query filter is an EF construct, so Dapper never sees it - which is why
// GetPriceCalendarHandler's Tier 3 statement (docs/adr/0014) restates the
// predicate by hand as `u.status <> @ArchivedStatus`. It once returned an
// archived unit's priced calendar for exactly that reason.
//
// The two agree today only because both are a single status comparison.
// Add a second condition to the filter - "and not expired", "and belongs to
// an active host" - and the hand-written copy silently stops matching it,
// with nothing in the change to say so and no test that fails, because every
// EF-based test would still pass. This asserts the shape the SQL twin
// assumes, so that edit breaks here and the author is told where the other
// copy lives rather than finding out from a bug.
//
// In-memory model only, no database: building the model is what applies the
// filter, same as ModelHasNoPendingChangesTests.
public class SoftDeleteFilterShapeTests
{
    private const string UnusedConnectionString =
        "Host=localhost;Database=staystack_probe;Username=postgres;Password=postgres;";

    private static IEntityType GetUnitEntityType()
    {
        var builder = new DbContextOptionsBuilder<AppCatalogDbContext>();
        builder.ConfigureStayStackDefaults(UnusedConnectionString, "catalog", false);
        using AppCatalogDbContext context = new AppCatalogDbContext(builder.Options);

        IEntityType? unit = context.Model.FindEntityType(typeof(Unit));
        Assert.NotNull(unit);
        return unit;
    }

    [Fact]
    public void TheSoftDeleteFilter_IsASingleStatusComparison_WhichIsWhatTheRawSqlTwinAssumes()
    {
        // GetDeclaredQueryFilters, not the obsolete single-filter
        // GetQueryFilter: EF 10 allows several named filters per type, and
        // "there is exactly one" is itself part of what the SQL twin assumes.
        // A second filter would not change the first one's shape, so checking
        // only that would miss it entirely.
        IReadOnlyCollection<IQueryFilter> filters = GetUnitEntityType().GetDeclaredQueryFilters();

        IQueryFilter filter = Assert.Single(filters);
        Assert.NotNull(filter.Expression);

        // e => e.Status != EntityStatus.Archived, and nothing else. If this
        // assertion fails, the filter gained structure - go and update
        // GetPriceCalendarHandler's `u.status <> @ArchivedStatus` to match
        // before changing this test.
        BinaryExpression comparison = Assert.IsAssignableFrom<BinaryExpression>(filter.Expression.Body);
        Assert.Equal(ExpressionType.NotEqual, comparison.NodeType);

        MemberExpression left = Assert.IsAssignableFrom<MemberExpression>(comparison.Left);
        Assert.Equal(nameof(Entity.Status), left.Member.Name);

        ConstantExpression right = Assert.IsAssignableFrom<ConstantExpression>(comparison.Right);
        Assert.Equal(EntityStatus.Archived, right.Value);
    }

    [Fact]
    public void TheArchivedOrdinal_IsWhatTheRawSqlPassesAsAParameter()
    {
        // The SQL twin binds @ArchivedStatus as (int)EntityStatus.Archived,
        // because Status is stored as its raw ordinal rather than converted
        // to a string. Reordering the enum would change the stored meaning of
        // every existing row, so this pins the ordinal the SQL depends on.
        Assert.Equal(2, (int)EntityStatus.Archived);
    }
}
