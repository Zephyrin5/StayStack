using Microsoft.EntityFrameworkCore;
using Persistence;
using Reviews.Entities.Configurations;
namespace Reviews;

public sealed class ReviewsModel : IModuleModel
{
    public void Configure(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new StayReviewConfiguration());
        builder.ApplyConfiguration(new GuestReviewConfiguration());
    }
}
