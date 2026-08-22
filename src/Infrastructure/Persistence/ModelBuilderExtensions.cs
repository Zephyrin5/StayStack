using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SeedWork.Abstractions;
using SeedWork.Enums;
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
}
