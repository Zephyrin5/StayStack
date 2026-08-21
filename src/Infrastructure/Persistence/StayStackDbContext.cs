using Microsoft.EntityFrameworkCore;
using Persistence.Comparers;
using Persistence.Converters;
using SeedWork.ValueObjects;
namespace Persistence;

/// <summary>
///     Every module's DbContext derives from this instead of DbContext
///     directly. OnModelCreating is sealed so there's no code path where a
///     new module's context compiles without the soft-delete filter -
///     forgetting to call ApplySoftDeleteQueryFilter() isn't possible,
///     because there's nowhere else to put the entity configuration calls.
/// </summary>
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
    }

    protected abstract void OnStayStackModelCreating(ModelBuilder modelBuilder);
}
