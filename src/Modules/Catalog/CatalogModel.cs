using Catalog.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Catalog;

public sealed class CatalogModel : IModuleModel
{
    /// <summary>This module's Postgres schema. Its tables and its raw SQL name it (docs/adr/0004).</summary>
    public const string Schema = "catalog";

    public void Configure(ModelBuilder builder)
    {
        // Backs PropertyConfiguration's GIN trigram index on City, which a leading-wildcard ILIKE needs.
        builder.HasPostgresExtension("pg_trgm");

        builder.ApplyConfiguration(new PropertyConfiguration());
        builder.ApplyConfiguration(new UnitConfiguration());
        builder.ApplyConfiguration(new PricingRuleConfiguration());
    }
}
