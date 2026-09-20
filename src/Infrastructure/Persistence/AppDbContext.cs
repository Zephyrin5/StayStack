using Microsoft.EntityFrameworkCore;
using Persistence.Comparers;
using Persistence.Converters;
using SeedWork.ValueObjects;
namespace Persistence;

/// <summary>
///     The application's one context. Its model is assembled from the modules' <see cref="IModuleModel"/>
///     contributions; module code reaches it through that module's own accessor, never directly.
/// </summary>
/// <remarks>
///     It declares no DbSet properties - it cannot reference the modules - so there is no name for EF to
///     infer a table from: every entity names its own table in its IEntityTypeConfiguration.
/// </remarks>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IEnumerable<IModuleModel> modules)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Required by the holds' GIST exclusion constraint (docs/adr/0010).
        modelBuilder.HasPostgresExtension("btree_gist");

        foreach (IModuleModel module in modules)
        {
            module.Configure(modelBuilder);
        }

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
}
