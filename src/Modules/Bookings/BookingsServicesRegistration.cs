using Bookings.Contracts;
using Bookings.Outbox;
using Catalog.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Persistence;
using Persistence.Interceptors;
namespace Bookings;

public static class BookingsServicesRegistration
{
    public static IServiceCollection ConfigureBookingsServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment? environment = null)
    {
        // Registered unconditionally, including under "Testing" - see the
        // note in IdentityServicesRegistration on why production
        // registration code shouldn't be the one deciding to skip itself
        // under tests. IntegrationTestWebApplicationFactory overrides this
        // DbContext's connection via RemoveAll + a fresh AddDbContext call.

        // Registered as a service so its own dependencies
        // (ICurrentUserProvider, TimeProvider) resolve through DI rather
        // than being newed up by hand.
        services.AddScoped<AuditableEntitySaveChangesInterceptor>();

        services.AddDbContext<AppBookingsDbContext>((serviceProvider, options) =>
        {
            string connectionString = configuration.GetConnectionString("AppConnection")
                                      ?? throw new InvalidOperationException(
                                          "Connection string for AppBookingsDbContext not found.");

            options.ConfigureStayStackDefaults(
                connectionString,
                "bookings",
                environment is not null && environment.IsDevelopment());

            options.AddInterceptors(serviceProvider.GetRequiredService<AuditableEntitySaveChangesInterceptor>());
        });

        services.AddScoped<IBookingLookup, BookingLookup>();
        services.AddScoped<IBookingPaymentConfirmation, BookingPaymentConfirmation>();
        services.AddScoped<BookingsOutboxDispatcher>();

        // Implements a Catalog-defined interface, not one of Bookings' own -
        // see IUnitArchivalGuard's own doc comment for why the interface
        // lives on the Catalog side of this relationship.
        services.AddScoped<IUnitArchivalGuard, UnitArchivalGuard>();

        return services;
    }
}
