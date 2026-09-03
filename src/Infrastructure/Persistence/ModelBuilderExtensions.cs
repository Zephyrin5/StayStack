using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SeedWork.Abstractions;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
namespace Persistence;

public static class ModelBuilderExtensions
{
    /// <summary>
    ///     Every entity deriving from Entity is automatically excluded from
    ///     normal queries once archived (soft-deleted) - callers never need
    ///     to remember "AND status != archived" on every query by hand, and
    ///     a new entity type gets this filter for free just by inheriting
    ///     from Entity, no per-type configuration required.
    ///     <para>
    ///         <b>This predicate is restated by hand in raw SQL.</b> A query
    ///         filter is an EF construct, so Dapper never sees it - the Tier 3
    ///         statement in <c>GetPriceCalendarHandler</c> carries its own
    ///         <c>u.status &lt;&gt; @ArchivedStatus</c> because of that, and
    ///         once returned an archived unit's priced calendar when it
    ///         didn't. If this filter ever gains a condition, that copy is
    ///         wrong from the moment yours is right, and nothing about the
    ///         change will point at it - the two agree today only because
    ///         both are a single status comparison.
    ///         <c>SoftDeleteFilterShapeTests</c> fails if that stops being
    ///         true, so the divergence is caught rather than discovered.
    ///     </para>
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "<Pending>")]
    [UnconditionalSuppressMessage("AOT",
        "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.",
        Justification = "<Pending>")]
    public static void ApplySoftDeleteQueryFilter(this ModelBuilder modelBuilder)
    {
        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(Entity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            ParameterExpression parameter = Expression.Parameter(entityType.ClrType, "e");
            MemberExpression statusProperty = Expression.Property(parameter, nameof(Entity.Status));
            ConstantExpression archivedValue = Expression.Constant(EntityStatus.Archived);
            BinaryExpression notArchived = Expression.NotEqual(statusProperty, archivedValue);
            LambdaExpression filter = Expression.Lambda(notArchived, parameter);

            entityType.SetQueryFilter(filter);
        }
    }

    /// <summary>
    ///     Maps a Money-typed complex property onto the same two plain
    ///     columns every money field used before Money existed - EF Core
    ///     10's native ComplexProperty (not OwnsOne/JSONB, unlike
    ///     LocalizedText/CancellationPolicy) pinned to explicit column
    ///     names, so introducing Money is a type-only change. numeric(12,3)
    ///     everywhere - scale 3 covers every supported currency (KWD needs
    ///     it) without truncation, and one shared width keeps every money
    ///     column consistent. See docs/adr/0015.
    /// </summary>
    public static void ConfigureMoney(
        this ComplexPropertyBuilder<Money> builder,
        string amountColumnName,
        string currencyColumnName = "currency")
    {
        builder.Property(m => m.Amount).HasColumnName(amountColumnName).HasColumnType("numeric(12,3)").IsRequired();
        builder.Property(m => m.Currency).HasColumnName(currencyColumnName).HasConversion<string>().HasMaxLength(3).IsRequired();
    }
}
