using Bookings.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Bookings;

public sealed class BookingsModel : IModuleModel
{
    public void Configure(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new BookingConfiguration());
        builder.ApplyConfiguration(new BookingManagementTokenConfiguration());
        builder.ApplyConfiguration(new CheckoutIdempotencyRecordConfiguration());
        builder.ApplyConfiguration(new RefundObligationConfiguration());
        builder.ApplyConfiguration(new UnitAvailabilityHoldConfiguration());
    }
}
