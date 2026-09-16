using Microsoft.EntityFrameworkCore;
using Persistence;
using Reviews.Entities.Configurations;
namespace Reviews;

public sealed class ReviewsModel : IModuleModel
{
    /// <summary>This module's Postgres schema. Its tables and its raw SQL name it (docs/adr/0004).</summary>
    public const string Schema = "reviews";

    public void Configure(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new StayReviewConfiguration());
        builder.ApplyConfiguration(new GuestReviewConfiguration());
    }
}
