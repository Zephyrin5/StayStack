using Microsoft.EntityFrameworkCore;
using Persistence;
using Promotions.Entities.Configurations;
namespace Promotions;

public sealed class PromotionsModel : IModuleModel
{
    public void Configure(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new PromotionConfiguration());
        builder.ApplyConfiguration(new PromotionRedemptionConfiguration());
    }
}
