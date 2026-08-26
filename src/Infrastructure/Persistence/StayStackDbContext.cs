using Microsoft.EntityFrameworkCore;
using Persistence.Comparers;
using Persistence.Converters;
using SeedWork.ValueObjects;
using System.Diagnostics.CodeAnalysis;
namespace Persistence;

/// <summary>
///     Every module's DbContext derives from this instead of DbContext
///     directly. OnModelCreating is sealed so there's no code path where a
///     new module's context compiles without the soft-delete filter -
///     forgetting to call ApplySoftDeleteQueryFilter() isn't possible,
///     because there's nowhere else to put the entity configuration calls.
/// </summary>
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2026:Using member 'Microsoft.EntityFrameworkCore.DbContext.DbContext(DbContextOptions)' which has 'RequiresUnreferencedCodeAttribute'",
    Justification = "EF Core is initialized safely for standard runtime.")]
[UnconditionalSuppressMessage(
    "AOT",
    "IL3050:Using member 'Microsoft.EntityFrameworkCore.DbContext.DbContext(DbContextOptions)' which has 'RequiresDynamicCodeAttribute'",
    Justification = "Not utilizing Native AOT execution.")]
public abstract class StayStackDbContext(DbContextOptions options) : DbContext(options)
{
    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        OnStayStackModelCreating(modelBuilder);
        modelBuilder.HasPostgresExtension("btree_gist");
        modelBuilder.ApplySoftDeleteQueryFilter();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<LocalizedText>()
            .HaveConversion<LocalizedTextConverter, LocalizedTextComparer>()
            .HaveColumnType("jsonb");

        configurationBuilder.Properties<CancellationPolicy>()
            .HaveConversion<CancellationPolicyConverter, CancellationPolicyComparer>()
            .HaveColumnType("jsonb");
    }

    protected abstract void OnStayStackModelCreating(ModelBuilder modelBuilder);
}
