using Bookings.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Bookings;

public sealed class BookingsModel : IModuleModel
{
    /// <summary>This module's Postgres schema. Its tables and its raw SQL name it (docs/adr/0004).</summary>
    public const string Schema = "bookings";

    public void Configure(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new BookingConfiguration());
        builder.ApplyConfiguration(new BookingManagementTokenConfiguration());
        builder.ApplyConfiguration(new CheckoutIdempotencyRecordConfiguration());
        builder.ApplyConfiguration(new RefundObligationConfiguration());
        builder.ApplyConfiguration(new UnitAvailabilityHoldConfiguration());
    }
}
