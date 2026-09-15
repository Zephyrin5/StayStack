using Bookings.Contracts;
using BuildingBlocks.Configuration;
using Bookings.Features.HoldAvailability;
using Bookings.Outbox;
using Catalog.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
        // Registered as a service so its own dependencies
        // (ICurrentUserProvider, TimeProvider) resolve through DI rather
        // than being newed up by hand.
        services.AddScoped<AuditableEntitySaveChangesInterceptor>();

        // Registered unconditionally, including under "Testing" - see the
        // note in IdentityServicesRegistration on why production
        // registration code shouldn't decide to skip itself under tests.
        // IntegrationTestWebApplicationFactory overrides this DbContext's
        // connection via RemoveAll + a fresh AddDbContext call.
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

        // IUnitAvailabilityLookup is a Catalog contract implemented here, the
        // inverted-interface pattern docs/adr/0004 uses for IUnitArchivalGuard
        // above: Catalog is upstream and needs availability for search and the
        // price calendar, so it declares and this module implements. No cycle -
        // the contracts projects are separate assemblies.
        services.AddOptions<HoldCapOptions>()
            .Bind(configuration.AppSection(HoldCapOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HoldCapOptions>, HoldCapOptionsValidator>();
        services.AddScoped<IHoldConfirmation, HoldConfirmation>();
        services.AddScoped<IUnitAvailabilityLookup, UnitAvailabilityLookup>();

        return services;
    }
}
