using Api.Common;
using Api.Configuration;
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

        // AllowCredentials() is what lets the browser carry the httpOnly refresh-token cookie, and it
        // is incompatible with AllowAnyOrigin() - fine, since origins are an explicit config list.
        //
        // CONSTRAINT before adding an origin: CORS and SameSite answer different questions, and only
        // CORS is about origins. An origin differing by port or path is the same *site*, so a Lax
        // cookie is still sent - that is the dev setup. An origin on another registrable domain or
        // scheme is cross-*site*: CORS allows it, the browser still refuses to attach the cookie, and
        // cookie auth fails with no error visible anywhere. That deployment sets Cookies:SameSite to
        // None and takes the CSRF exposure (CookieSecurityOptions.SameSite).
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
            .ValidateOnStart();
        // Both run in the same ValidateOnStart pass: the generated validator
        // covers the per-field rules, LocalizationSettingsValidator the one that
        // spans two fields.
        services.AddSingleton<IValidateOptions<LocalizationSettings>, LocalizationSettingsAttributesValidator>();
        // See LocalizationSettingsValidator for why it isn't a post-Build() check
        // in Program.cs like the other two cross-field invariants.
        services.AddSingleton<IValidateOptions<LocalizationSettings>, LocalizationSettingsValidator>();

        // The same bound options feed the request pipeline, rather than the
        // same keys read again as raw strings.
        LocalizationSettings localization =
            configuration.AppSection(LocalizationSettings.SectionName).Get<LocalizationSettings>()
            ?? new LocalizationSettings();

        services.Configure<RequestLocalizationOptions>(options =>
        {
            // Never empty: the ValidateOnStart registration above rejects that
            // at boot.
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
        // CS8620: the generated lists are List<Type>, and FastEndpoints' params parameter is
        // List<Type>[] with different nullability on the element type. Nothing here can satisfy both.
#pragma warning disable CS8620
        services.AddFastEndpoints(
            IdentityDiscoveredTypes.All,
            CatalogDiscoveredTypes.All,
            HostsDiscoveredTypes.All,
            BookingsDiscoveredTypes.All,
            ReviewsDiscoveredTypes.All,
            TransactionsDiscoveredTypes.All,
            DiscoveredTypes.All);
#pragma warning restore CS8620
        // The combined source-generated resolver (each module's own DTOs
        // plus a reflection fallback) is wired onto Config.Serializer.Options
        // in Program.cs's UseFastEndpoints call instead of here - that's an
        // app-building-stage (IApplicationBuilder) setting, not a
        // service-registration-stage (IServiceCollection) one.

        services.AddMediator(options => { options.ServiceLifetime = ServiceLifetime.Scoped; });

        // In-process (L1) only, wrapping Catalog's three read paths. Bounded explicitly because each
        // is anonymous and keyed on query parameters, so the number of distinct entries is chosen by
        // the caller rather than by this application.
        //
        // SizeLimit is in bytes, which is what HybridCache writes as each entry's Size; with
        // MaximumPayloadBytes bounding one entry, the pair bounds the whole L1.
        //
        // THE RULE THIS LINE CREATES (docs/adr/0024): with SizeLimit set, *every* IMemoryCache entry
        // must specify a Size in bytes, or MemoryCache throws on the first request that writes one -
        // not at startup. MemoryCacheSizeRuleTests fails if a component arrives without having read
        // this.
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
