using BuildingBlocks.Persistence;
using Bookings.Contracts;
using BuildingBlocks.Configuration;
using Bookings.Features.HoldAvailability;
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

        services.AddSingleton<IModuleModel, BookingsModel>();
        services.AddScoped<BookingsDb>();

        services.AddScoped<IBookingLookup, BookingLookup>();
        services.AddScoped<IBookingPaymentConfirmation, BookingPaymentConfirmation>();

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
