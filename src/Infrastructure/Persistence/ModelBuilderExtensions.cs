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
    ///     columns every money field used before Money existed - no
    ///     OwnsOne, no JSONB collapse (unlike LocalizedText/CancellationPolicy,
    ///     the only other converted-type precedents in this codebase), just
    ///     EF Core 10's native ComplexProperty support pinned to explicit
    ///     column names so introducing Money is a type-only change against
    ///     already-existing columns wherever the names match. numeric(12,3)
    ///     everywhere - scale 3 covers every currency this app supports
    ///     (KWD needs it) without truncation, and one shared width means
    ///     every money column agrees rather than each entity's config
    ///     picking its own precision by hand. See docs/adr/0015.
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
