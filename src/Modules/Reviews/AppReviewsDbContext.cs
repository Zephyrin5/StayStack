using Microsoft.EntityFrameworkCore;
using Persistence;
using Reviews.Entities;
using Reviews.Entities.Configurations;
namespace Reviews;

// Kept only so this module's existing migrations compile; nothing registers or uses it. The
// model lives in the module's IModuleModel, and this class goes when the migrations are squashed.
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
