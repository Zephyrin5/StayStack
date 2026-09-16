using Microsoft.EntityFrameworkCore;
using Persistence;
using Promotions.Entities.Configurations;
namespace Promotions;

public sealed class PromotionsModel : IModuleModel
{
    /// <summary>This module's Postgres schema. Its tables and its raw SQL name it (docs/adr/0004).</summary>
    public const string Schema = "promotions";

    public void Configure(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new PromotionConfiguration());
        builder.ApplyConfiguration(new PromotionRedemptionConfiguration());
    }
}
