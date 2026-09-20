using Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Persistence;
using SeedWork.Abstractions;
using SeedWork.Enums;
using System.Linq.Expressions;
namespace UnitTests.Persistence;

// Archived rows are excluded from every query by a filter each entity's configuration applies. It
// used to be applied to every Entity subtype by a convention that discovered them at runtime, which
// is reflection the trimmer cannot follow; what the convention guaranteed - that none is missed - is
// now this test's job.
//
// The second thing it guards is the filter's shape. A query filter is an EF construct, so Dapper
// never sees it: Persistence.SoftDelete restates the same predicate by hand for raw SQL and partial
// index filters. The two agree only because both are a single status comparison. Add a condition to
// the filter - "and not expired", "and belongs to an active host" - and the SQL copy silently stops
// matching, with nothing in the change to say so and every EF-based test still passing.
//
// In-memory model only, no database: building the model is what applies the filters.
public class SoftDeleteFilterTests
{
    private const string UnusedConnectionString =
        "Host=localhost;Database=staystack_probe;Username=postgres;Password=postgres;";

    private static IModel BuildModel()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.ConfigureStayStackDefaults(UnusedConnectionString, "app", false);
        using AppDbContext context = new AppDbContext(builder.Options, AppDbContextModels.All);

        return context.Model;
    }

    [Fact]
    public void EveryEntityType_ExcludesArchivedRows_WithASingleStatusComparison()
    {
        List<IEntityType> entities =
        [
            .. BuildModel().GetEntityTypes().Where(type => typeof(Entity).IsAssignableFrom(type.ClrType))
        ];

        // Not vacuous: the model is assembled from every module, and each contributes entities.
        Assert.True(entities.Count >= 9, $"Found only {entities.Count} Entity-derived types - the scan is broken.");

        foreach (IEntityType entity in entities)
        {
            // GetDeclaredQueryFilters, not the obsolete single-filter GetQueryFilter: EF 10 allows
            // several named filters per type, and "there is exactly one" is itself part of what the
            // SQL twin assumes. A second filter would not change the first one's shape.
            IQueryFilter filter = Assert.Single(entity.GetDeclaredQueryFilters());
            Assert.NotNull(filter.Expression);

            // e => e.Status != EntityStatus.Archived, and nothing else. If this fails, update
            // Persistence.SoftDelete - and every statement built from it - before changing this test.
            BinaryExpression comparison = Assert.IsAssignableFrom<BinaryExpression>(filter.Expression.Body);
            Assert.Equal(ExpressionType.NotEqual, comparison.NodeType);

            MemberExpression left = Assert.IsAssignableFrom<MemberExpression>(Unwrap(comparison.Left));
            Assert.Equal(nameof(Entity.Status), left.Member.Name);

            // Compared as the ordinal, which is both what the model stores and what SoftDelete's SQL
            // writes - the enum is converted away by the time the filter reaches the model.
            ConstantExpression right = Assert.IsAssignableFrom<ConstantExpression>(Unwrap(comparison.Right));
            Assert.Equal(SoftDelete.ArchivedStatus, Convert.ToInt32(right.Value));
        }
    }

    // The helper is generic over TEntity : Entity, so the compiler converts the parameter to Entity
    // to reach Status. EF translates the same `status <> 2` either way; the cast is not a condition.
    private static Expression Unwrap(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : expression;

    [Fact]
    public void TheArchivedOrdinal_IsWhatTheRawSqlPassesAsAParameter()
    {
        // SoftDelete builds its predicate from the enum, and Status is stored as the raw ordinal:
        // reordering EntityStatus would change the stored meaning of every existing row.
        Assert.Equal(2, SoftDelete.ArchivedStatus);
        Assert.Equal("status <> 2", SoftDelete.NotArchived);
    }
}
