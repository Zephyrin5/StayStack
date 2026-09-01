using Api.Common;
using Api.Localization;
using Api.Security;
using Api.Serialization;
using BuildingBlocks.Identity;
using BuildingBlocks.Localization;
using FastEndpoints;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Localization;
using BookingsDiscoveredTypes = Bookings.DiscoveredTypes;
using CatalogDiscoveredTypes = Catalog.DiscoveredTypes;
using HostsDiscoveredTypes = Hosts.DiscoveredTypes;
using IdentityDiscoveredTypes = Identity.DiscoveredTypes;
using ReviewsDiscoveredTypes = Reviews.DiscoveredTypes;
using TransactionsDiscoveredTypes = Transactions.DiscoveredTypes;

namespace Api;

public static class ApiServicesRegistration
{
    public const string ClientAppCorsPolicy = "ClientApp";
    public const string AuthRateLimitPolicy = "auth";
    public const string HoldRateLimitPolicy = "holds";

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
        //
        // CONSTRAINT worth knowing before adding an origin here: CORS and
        // SameSite are answering different questions, and only CORS is about
        // origins. An origin that differs only by port or path is still the
        // same *site*, so the SameSite=Lax cookie is sent normally - that is
        // exactly the dev setup (localhost:3000 -> localhost:5277), which
        // needs AllowCredentials precisely because it IS cross-origin.
        //
        // An origin on a different registrable domain, or a different scheme,
        // is cross-*site*. CORS will happily allow it and the browser will
        // still refuse to attach a Lax cookie, so cookie-mode auth fails with
        // no error visible anywhere - refresh simply 401s. Such a deployment
        // has to set Cookies:SameSite to None (which requires
        // Cookies:RequireSecure, enforced at startup) and accept the CSRF
        // exposure that comes with it. See CookieSecurityOptions.SameSite.
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
        // source-generated DiscoveredTypes list passed explicitly - once
        // any list is passed, FastEndpoints stops reflection-scanning
        // assemblies it wasn't given, so an omitted module's validators
        // silently never fire (a request reaches the handler with
        // unvalidated data instead of failing with 400 - caught via a
        // Bookings HTTP test expecting 400, getting 500).
        services.AddFastEndpoints(
            IdentityDiscoveredTypes.All,
            CatalogDiscoveredTypes.All,
            HostsDiscoveredTypes.All,
            BookingsDiscoveredTypes.All,
            ReviewsDiscoveredTypes.All,
            TransactionsDiscoveredTypes.All,
            DiscoveredTypes.All);
        // The combined source-generated resolver (each module's own DTOs
        // plus a reflection fallback) is wired onto Config.Serializer.Options
        // in Program.cs's UseFastEndpoints call instead of here - that's an
        // app-building-stage (IApplicationBuilder) setting, not a
        // service-registration-stage (IServiceCollection) one.

        services.AddMediator(options => { options.ServiceLifetime = ServiceLifetime.Scoped; });

        // In-process (L1) cache only, no L2 registered. Wraps the
        // GetPriceCalendarHandler/GetPropertiesHandler/GetPropertyByIdHandler
        // read paths.
        services.AddHybridCache();

        return services;
    }
}
