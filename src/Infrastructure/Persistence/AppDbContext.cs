using Microsoft.EntityFrameworkCore;
using Persistence.Comparers;
using Persistence.Converters;
using SeedWork.ValueObjects;
namespace Persistence;

/// <summary>
///     The application's one context. Its model is assembled from the modules' <see cref="IModuleModel"/>
///     contributions; module code reaches it through that module's own accessor, never directly.
/// </summary>
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

        // Last, so no module can be configured without it.
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
}
