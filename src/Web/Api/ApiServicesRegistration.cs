using Api.Common;
using Api.Localization;
using Api.Security;
using Bookings.Contracts;
using Api.Serialization;
using BuildingBlocks.Configuration;
using BuildingBlocks.Identity;
using BuildingBlocks.Localization;
using FastEndpoints;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
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

    // The anonymous read endpoints. Separate from the two above because it is
    // answering a different question - those bound how often a caller may
    // change something, this bounds how often they may ask - and because its
    // limit has to be orders of magnitude looser to stay invisible to real
    // browsing. See ReadRateLimitOptions.
    public const string ReadRateLimitPolicy = "reads";

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
        string[] allowedOrigins = configuration.AppSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
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

        // Bound here, and this registration was missing entirely. Handlers
        // across every module inject IOptions<LocalizationSettings> to know
        // which culture LocalizedText.Create requires - and IOptions<T>
        // resolves whether or not anything configured T, handing back a
        // default-constructed instance. Because the type's own defaults
        // matched appsettings, nothing looked wrong while the section went
        // unread. Changing App:Localization:DefaultCulture would have moved
        // culture negotiation below without moving what LocalizedText demands,
        // so one setting would have meant two different things.
        services.AddOptions<LocalizationSettings>()
            .Bind(configuration.AppSection(LocalizationSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        // Picked up by the same ValidateOnStart pass above - ValidateDataAnnotations
        // covers the per-field rules, this covers the one that spans two fields.
        // See LocalizationSettingsValidator for why it isn't a post-Build() check
        // in Program.cs like the other two cross-field invariants.
        services.AddSingleton<IValidateOptions<LocalizationSettings>, LocalizationSettingsValidator>();

        // The same bound shape feeds the request pipeline, rather than reading
        // the same keys again as raw strings. That duplication is what let the
        // two drift apart in the first place, and it was the only caller of
        // AppConfiguration.AppValue - a string-path helper with no
        // compile-time safety, where a typo silently returned null and fell
        // through to the ?? default. It is gone with its last caller.
        LocalizationSettings localization =
            configuration.AppSection(LocalizationSettings.SectionName).Get<LocalizationSettings>()
            ?? new LocalizationSettings();

        services.Configure<RequestLocalizationOptions>(options =>
        {
            // No fallback for an empty SupportedCultures any more - the
            // ValidateOnStart registration above rejects that at boot, so
            // the branch that used to substitute a hardcoded ["en", "ar"]
            // could only ever run in a host that never finished starting.
            // It also meant a cleared or misspelled section quietly got the
            // right answer from C# instead of a startup failure.
            string[] supportedCultures = localization.SupportedCultures;
            options.SetDefaultCulture(localization.DefaultCulture)
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
        services.AddScoped<IBookingSessions, BookingSessions>();
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
        //
        // Bounded explicitly rather than left on defaults, because every one
        // of those read paths is anonymous, unthrottled, and keyed on query
        // parameters - so the number of distinct cache entries is chosen by
        // the caller, not by this application. The per-request validators cap
        // how many keys can exist (see GetPriceCalendarRequestValidator's date
        // bounds and PaginationDefaults.MaxOffset); these cap what the cache
        // costs even if a future endpoint arrives without such a cap.
        //
        // SizeLimit is in bytes: HybridCache sets each L1 entry's Size to its
        // serialized length. That pairs with MaximumPayloadBytes below, which
        // bounds one entry, to bound the whole L1 as well - a limit on entry
        // size alone still admits unlimited entries. Nothing else in this
        // application resolves IMemoryCache, so this budget is HybridCache's
        // alone.
        services.AddMemoryCache(options => options.SizeLimit = 64 * 1024 * 1024);

        services.AddHybridCache(options =>
        {
            // Comfortably above the largest legitimate payload - a 366-day
            // price calendar runs tens of KB, a 100-item property page under a
            // couple hundred - and far below anything worth holding. An
            // oversized payload is simply not cached rather than being an
            // error, so this degrades to a cache miss, never a 500.
            options.MaximumPayloadBytes = 512 * 1024;

            // The keys are interpolated from route and query values. The
            // longest legitimate one is GetProperties' (a normalized city
            // plus six short fields); 1024 is the library default and is
            // stated here so the bound is visible next to the reason it
            // matters rather than being inherited silently.
            options.MaximumKeyLength = 1024;
        });

        return services;
    }
}
