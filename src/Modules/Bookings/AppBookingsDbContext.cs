using Bookings.Entities;
using Bookings.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Bookings;

public class AppBookingsDbContext(DbContextOptions<AppBookingsDbContext> options) : StayStackDbContext(options)
{
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingManagementToken> BookingManagementTokens => Set<BookingManagementToken>();

    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new BookingConfiguration());
        modelBuilder.ApplyConfiguration(new BookingManagementTokenConfiguration());
    }
}
