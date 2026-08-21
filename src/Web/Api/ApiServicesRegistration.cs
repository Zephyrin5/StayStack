using Api.Common;
using Api.Localization;
using Api.Security;
using BuildingBlocks.Identity;
using BuildingBlocks.Localization;
using FastEndpoints;
using Microsoft.AspNetCore.Localization;
namespace Api;

public static class ApiServicesRegistration
{
    public static IServiceCollection ConfigureApiServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<ICurrentLanguageProvider, CultureInfoLanguageProvider>();

        services.Configure<RequestLocalizationOptions>(options =>
        {
            string[] supportedCultures = configuration.GetSection("Localization:SupportedCultures").Get<string[]>() ?? ["en", "ar"];
            options.SetDefaultCulture(configuration["Localization:DefaultCulture"] ?? "en")
                .AddSupportedCultures(supportedCultures)
                .AddSupportedUICultures(supportedCultures);
            options.RequestCultureProviders =
            [
                new QueryStringRequestCultureProvider { QueryStringKey = "lang" },
                new ClaimsRequestCultureProvider(),
                new AcceptLanguageHeaderRequestCultureProvider()
            ];
        });

        services.AddScoped<ICurrentUserProvider, HttpContextCurrentUserProvider>();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddFastEndpoints(
            Identity.DiscoveredTypes.All,
            DiscoveredTypes.All);
        // The combined source-generated resolver (each module's own DTOs
        // plus a reflection fallback) is wired onto Config.Serializer.Options
        // in Program.cs's UseFastEndpoints call instead of here - that's an
        // app-building-stage (IApplicationBuilder) setting, not a
        // service-registration-stage (IServiceCollection) one.

        services.AddMediator(options => { options.ServiceLifetime = ServiceLifetime.Scoped; });

        // In-process (L1) cache only, no L2 registered - see the hosting
        // constraints discussion. Wraps the price calendar read path in
        // GetPriceCalendarHandler.
        services.AddHybridCache();

        return services;
    }
}
