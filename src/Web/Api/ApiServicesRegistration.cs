using Api.Common;
using Api.Localization;
using Api.Security;
using Api.Serialization;
using BuildingBlocks.Identity;
using BuildingBlocks.Localization;
using FastEndpoints;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Localization;
namespace Api;

using BookingsDiscoveredTypes = Bookings.DiscoveredTypes;
using CatalogDiscoveredTypes = Catalog.DiscoveredTypes;
using HostsDiscoveredTypes = Hosts.DiscoveredTypes;
using IdentityDiscoveredTypes = Identity.DiscoveredTypes;

public static class ApiServicesRegistration
{
    public const string ClientAppCorsPolicy = "ClientApp";

    public static IServiceCollection ConfigureApiServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<ICurrentLanguageProvider, CultureInfoLanguageProvider>();

        // AllowCredentials() is required for the browser to send/receive
        // the httpOnly refresh-token cookie (see Api.Security.AuthCookies,
        // cookie-mode auth) - incompatible with AllowAnyOrigin(), which is
        // fine since origins are already an explicit config list, never a
        // wildcard. Origins come from config rather than being hardcoded
        // so prod can set a different list without a code change.
        string[] allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(options =>
        {
            options.AddPolicy(ClientAppCorsPolicy, policy =>
            {
                policy.WithOrigins(allowedOrigins)
                    .AllowAnyHeader()
                    .WithMethods("GET", "POST", "PUT", "DELETE")
                    .AllowCredentials();
            });
        });

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

        // WriteAsJsonAsync (GlobalExceptionHandler) and Results.Problem (the
        // 404 page in Program.cs) don't go through FastEndpoints, so they'd
        // otherwise fall back to ASP.NET Core's own fully-reflection-based
        // default resolver even though every type they actually serialize
        // is already covered by ApiJsonTypeInfoResolver.Combined.
        services.Configure<JsonOptions>(o =>
            o.SerializerOptions.TypeInfoResolver = ApiJsonTypeInfoResolver.Combined);

        services.AddScoped<ICurrentUserProvider, HttpContextCurrentUserProvider>();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        // Every module with its own Endpoint/Validator types needs its
        // source-generated DiscoveredTypes list passed explicitly - once any
        // list is passed, FastEndpoints stops reflection-scanning assemblies
        // it wasn't given, so an omitted module's validators silently never
        // fire (a request just reaches the handler with unvalidated data
        // instead of failing with 400 - caught via a Bookings HTTP test that
        // expected 400 and got 500 instead).
        services.AddFastEndpoints(
            IdentityDiscoveredTypes.All,
            CatalogDiscoveredTypes.All,
            HostsDiscoveredTypes.All,
            BookingsDiscoveredTypes.All,
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
