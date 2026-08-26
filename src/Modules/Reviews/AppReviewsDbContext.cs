using Microsoft.EntityFrameworkCore;
using Persistence;
using Reviews.Entities;
using Reviews.Entities.Configurations;
namespace Reviews;

public class AppReviewsDbContext(DbContextOptions<AppReviewsDbContext> options) : StayStackDbContext(options)
{
    public DbSet<StayReview> StayReviews => Set<StayReview>();
    public DbSet<GuestReview> GuestReviews => Set<GuestReview>();

    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new StayReviewConfiguration());
        modelBuilder.ApplyConfiguration(new GuestReviewConfiguration());
    }
}
