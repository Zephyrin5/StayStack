using Api;
using Api.RateLimiting;
using Api.Security;
using Api.Serialization;
using Bookings;
using Bookings.Contracts;
using Bookings;
using BuildingBlocks.Configuration;
using Catalog;
using Catalog.Contracts;
using FastEndpoints;
using FastEndpoints.OpenApi;
using Hosts;
using Identity;
using Jobs;
using Promotions;
using Reviews;
using TickerQ.DependencyInjection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Persistence;
using Scalar.AspNetCore;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Transactions;
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseKestrelHttpsConfiguration();

// CreateSlimBuilder, unlike CreateBuilder, doesn't wire up user-secrets by
// default - added explicitly so local connection strings/keys can live in
// the Secret Manager instead of appsettings.json.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(true);
}

builder.Services.ConfigureIdentityServices(builder.Configuration, builder.Environment);
// TODO: Disabled until Grafana is configured
//builder.Services.ConfigureObservabilityServices(builder.Configuration);
builder.Services.ConfigurePersistenceServices();
builder.Services.ConfigureApiServices(builder.Configuration);
builder.Services.ConfigureCatalogServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureHostsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigurePromotionsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureBookingsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureReviewsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureTransactionsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureJobsServices(builder.Configuration, builder.Environment);
// Post-stay deadlines: how long a stay stays reviewable, and how long a
// guest-checkout management link stays usable. Two settings rather than one
// because they answer different questions - see
// BookingLifecyclePolicyOptions, and the consistency check after Build().
builder.Services.AddOptions<BookingLifecyclePolicyOptions>()
    .Bind(builder.Configuration.AppSection(BookingLifecyclePolicyOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
// How far ahead a stay can start and how long it can run - read by the search
// path (Catalog) and the hold path (Availability) alike, so the two cannot
// disagree about what is bookable. No consistency check after Build() to pair
// with this one: there is a single value per bound rather than two that have
// to be kept in step. See StaySearchPolicyOptions.
builder.Services.AddOptions<StaySearchPolicyOptions>()
    .Bind(builder.Configuration.AppSection(StaySearchPolicyOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHealthChecks();

// Fixed-window, keyed by caller IP - auth and payment-initiation endpoints
// are the obvious credential-stuffing/abuse targets and had no
// application-level limiting at all. RequireRateLimiting("auth") is
// applied per-endpoint via Options() in Configure() (SignInEndpoint,
// RegisterEndpoint, RefreshTokenEndpoint, InitiateTransactionEndpoint).
//
// Limit/window resolved per partition from IOptions<AuthRateLimitOptions>,
// not captured once at startup, so tests can override it via the
// standard Configure<AuthRateLimitOptions> DI-replacement pattern:
// appsettings.Testing.json sets a high limit so the shared integration-
// test factory doesn't trip it on ordinary traffic; RateLimitingTests
// overrides it back down to actually exercise a 429.
builder.Services.AddOptions<CookieSecurityOptions>()
    .Bind(builder.Configuration.AppSection(CookieSecurityOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<AuthRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(AuthRateLimitOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
// Each policy binds its own nested section under RateLimiting rather than
// sharing one and prefixing property names to stay out of each other's way -
// see AuthRateLimitOptions.SectionName.
builder.Services.AddOptions<HoldRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(HoldRateLimitOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<ReadRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(ReadRateLimitOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(ApiServicesRegistration.AuthRateLimitPolicy, httpContext =>
    {
        AuthRateLimitOptions limits = httpContext.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;

        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.PermitLimit,
                Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                QueueLimit = 0
            });
    });

    // HoldAvailabilityEndpoint is anonymous and DB-write - the caps in
    // HoldAvailabilityRequestValidator/HoldAvailabilityHandler bound how
    // much damage one hold can do, this bounds how many a caller can fire.
    // Partitioned by RemoteIpAddress, same as "auth", not by the
    // hold-session cookie - a scripted caller can drop and regenerate
    // that per request, so it would be no partition at all as a
    // rate-limit key.
    options.AddPolicy(ApiServicesRegistration.HoldRateLimitPolicy, httpContext =>
    {
        HoldRateLimitOptions limits = httpContext.RequestServices.GetRequiredService<IOptions<HoldRateLimitOptions>>().Value;

        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.PermitLimit,
                Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                QueueLimit = 0
            });
    });

    // The anonymous read endpoints - GetProperties, GetPropertyById,
    // GetPriceCalendar, GetPropertyReviews - which had no limiter at all.
    // Their per-request cost is bounded (the stay-window caps, the price
    // calendar's date bounds, MaxOffset, the HybridCache limits); nothing
    // bounded how many of them a caller could issue.
    //
    // Same IP partition as the other two, and the same caveat that makes
    // this limit deliberately loose: with ForwardedHeaders.KnownProxies
    // unset every caller shares the proxy's address, so a tight limit here
    // would stop real guests browsing rather than stop abuse. See
    // ReadRateLimitOptions.
    options.AddPolicy(ApiServicesRegistration.ReadRateLimitPolicy, httpContext =>
    {
        ReadRateLimitOptions limits = httpContext.RequestServices.GetRequiredService<IOptions<ReadRateLimitOptions>>().Value;

        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.PermitLimit,
                Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                QueueLimit = 0
            });
    });
});

builder.Services.AddHttpContextAccessor();

builder.Services.OpenApiDocument(o =>
{
    o.DocumentName = "api";
    o.Title = "StayStack API";
    o.Version = "v1";
    o.AutoTagPathSegmentIndex = 0;

    // x-tagGroups is a Scalar/ReDoc vendor extension, not a FastEndpoints
    // concept - added via the document-transformer hook instead. Nests
    // Catalog's per-family tags (CatalogGroup.cs, each endpoint's own
    // Description(b => b.WithTags(...))) under one collapsible "Catalog"
    // heading rather than a flat list of a dozen-plus tagged operations.
    //
    // IMPORTANT: once x-tagGroups is present, Scalar stops showing any tag
    // not listed in SOME group - it doesn't fall back to a flat top-level
    // section, it silently disappears from the sidebar (confirmed by
    // diffing the rendered sidebar against the raw document's tag list).
    // A new module/tag added later needs a line added here too, or its
    // docs go dark with no other symptom.
    o.ConfigureOpenApi = openApiOptions =>
    {
        openApiOptions.AddDocumentTransformer((document, _, _) =>
        {
            document.Extensions ??= new Dictionary<string, IOpenApiExtension>();
            document.Extensions["x-tagGroups"] = new JsonNodeExtension(JsonNode.Parse(
                """
                [
                    { "name": "Catalog", "tags": ["Properties", "Units", "Pricing Rules"] },
                    { "name": "Availability", "tags": ["Availability"] },
                    { "name": "Promotions", "tags": ["Promotions"] },
                    { "name": "Hosts", "tags": ["Hosts"] },
                    { "name": "Bookings", "tags": ["Bookings"] },
                    { "name": "Transactions", "tags": ["Transactions"] },
                    { "name": "Auth", "tags": ["Auth"] },
                    { "name": "Users", "tags": ["Users"] },
                    { "name": "Localization", "tags": ["Localization"] }
                ]
                """)!);
            return Task.CompletedTask;
        });
    };
});

WebApplication app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    app.MapScalarApiReference("api/docs", options =>
    {
        options
            .WithTitle("StayStack API")
            .WithTheme(ScalarTheme.None)
            .AddDocument("api")
            .WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.HttpClient);
    });
}

// Populated from config, never hardcoded, so each deployment lists its
// actual proxy addresses. Registered before anything else reads
// Request.IsHttps or Connection.RemoteIpAddress.
//
// This does NOT trust nothing by default, despite what this comment used to
// claim: ForwardedHeadersOptions ships with KnownProxies = { ::1 } and
// KnownNetworks = { 127.0.0.0/8 }, and the loop below only adds to them. So
// with the shipped empty config, a loopback caller is trusted and every
// other address is not.
//
// The consequence is the reason ForwardedHeaders:KnownProxies has to be
// populated in any proxied deployment. Without it, a TLS-terminating proxy
// at a non-loopback address has its headers dropped, and two controls read
// the wrong thing: RemoteIpAddress becomes the proxy's own address, so the
// "holds"/"auth" rate-limit partitions and HoldAvailabilityHandler's
// concurrent-hold cap collapse into one shared bucket for every caller.
// AuthCookies used to be a third victim - its Secure flag is now declared
// by configuration instead (see CookieSecurityOptions), precisely because a
// security flag should not depend on transport details the app may not be
// able to see.
//
// The startup check below makes that misconfiguration loud rather than
// silent.
ForwardedHeadersOptions forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
foreach (string proxy in app.Configuration.AppSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
{
    forwardedHeadersOptions.KnownProxies.Add(IPAddress.Parse(proxy));
}
// A throw for the same reason as the SameSite check below: this misconfiguration
// produces a silently wrong product rule rather than a visible failure. A
// management token that dies before the review window closes puts guest
// checkout back exactly where it started - able to review in principle, locked
// out of its own booking in practice - and the only symptom is guests quietly
// not reviewing.
BookingLifecyclePolicyOptions bookingLifecycle =
    app.Services.GetRequiredService<IOptions<BookingLifecyclePolicyOptions>>().Value;
if (bookingLifecycle.ManagementTokenLifetimeDaysAfterCheckOut < bookingLifecycle.ReviewWindowDaysAfterCheckOut)
{
    throw new InvalidOperationException(
        $"{BookingLifecyclePolicyOptions.SectionName}:ManagementTokenLifetimeDaysAfterCheckOut " +
        $"({bookingLifecycle.ManagementTokenLifetimeDaysAfterCheckOut}) is shorter than " +
        $"ReviewWindowDaysAfterCheckOut ({bookingLifecycle.ReviewWindowDaysAfterCheckOut}). " +
        "Guest-checkout callers would lose access to their booking before the review window closes, so " +
        "the window would apply only to signed-in customers - which is the asymmetry these settings exist " +
        "to remove. Raise the token lifetime to at least the review window.");
}

// A throw, not a warning, unlike the proxy check below: SameSite=None
// without Secure is refused by every modern browser, so the cookie is never
// stored and cookie-mode auth cannot work at all. There is no deployment
// where this combination is what someone meant, which makes starting up and
// serving broken sessions strictly worse than refusing to start.
CookieSecurityOptions cookieSecurity =
    app.Services.GetRequiredService<IOptions<CookieSecurityOptions>>().Value;
if (cookieSecurity.SameSite == SameSiteMode.None && !cookieSecurity.RequireSecure)
{
    throw new InvalidOperationException(
        $"{CookieSecurityOptions.SectionName}:SameSite is None but RequireSecure is false. Browsers reject " +
        "SameSite=None cookies that are not Secure, so no session cookie would ever be stored. A cross-site " +
        "SPA needs both; a same-site one should leave SameSite at Lax.");
}

// The other half of the SameSite/CORS pair, and the half that fails quietly.
// The check above catches a combination browsers reject outright; this one
// catches a combination they accept and then ignore - CORS allows the origin,
// the preflight passes, and the cookie is simply never attached, so cookie-mode
// auth 401s with nothing wrong in any log.
//
// A throw outside Development, matching the checks above: a deployment that
// listed a cross-site origin *and* left SameSite at Lax has asked for two
// things that cannot both be true, and serving sessions that silently do not
// work is worse than refusing to start. Development is exempt because the
// localhost:3000 -> localhost:5277 split is same-site anyway and would never
// trip this.
string[] corsOrigins = app.Configuration.AppSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

if (corsOrigins.Length > 0 && cookieSecurity.SameSite == SameSiteMode.Lax)
{
    if (string.IsNullOrWhiteSpace(cookieSecurity.ApiOrigin))
    {
        // Cannot verify rather than verified-fine, and said out loud once.
        // Staying silent here would read as "checked, all good".
        app.Logger.LogWarning(
            "{Section}:ApiOrigin is not set, so the CORS/SameSite consistency check cannot run. If any origin in " +
            "{CorsSection}:AllowedOrigins is on a different registrable domain or scheme than this API, the " +
            "SameSite=Lax cookies it sets will never be attached to that origin's requests and cookie-mode auth " +
            "will fail with no visible error. Bearer tokens are unaffected.",
            CookieSecurityOptions.SectionName, "Cors");
    }
    else
    {
        string[] crossSite =
        [
            .. corsOrigins.Where(origin => !SameSiteOriginCheck.IsSameSite(origin, cookieSecurity.ApiOrigin))
        ];

        if (crossSite.Length > 0 && !app.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{CookieSecurityOptions.SectionName}:SameSite is Lax, but these allowed CORS origins are " +
                $"cross-site with {CookieSecurityOptions.SectionName}:ApiOrigin " +
                $"({cookieSecurity.ApiOrigin}): {string.Join(", ", crossSite)}. A browser will not attach a Lax " +
                "cookie to their requests, so cookie-mode auth cannot work for them and refresh would fail with " +
                "no error visible anywhere. Either serve the SPA same-site with the API, or leave those clients " +
                "on bearer tokens (the default - omit ?useCookies=true), or set SameSite to None with " +
                "RequireSecure and accept that you now own CSRF protection on every cookie-authenticated " +
                "endpoint.");
        }
    }
}

if (!app.Environment.IsDevelopment() && forwardedHeadersOptions.KnownProxies.Count == 0)
{
    // A warning, not a throw: an app exposed directly with its own TLS has
    // no proxy to list, and that is a legitimate deployment. But it is far
    // more often an oversight, and the symptom - every caller sharing one
    // rate-limit and hold-cap partition - reads as mysterious 429s rather
    // than as a configuration problem, so it is worth saying plainly once
    // at startup.
    app.Logger.LogWarning(
        "App:ForwardedHeaders:KnownProxies is empty outside Development. Only loopback proxies are trusted, " +
        "so behind a proxy at any other address X-Forwarded-For/-Proto are ignored: every caller will share " +
        "one rate-limit and concurrent-hold partition keyed on the proxy's address. List the proxy addresses " +
        "if this app is deployed behind one.");
}

app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseRequestLocalization();

app.UseHttpsRedirection();

// Registered before the /api exception-handler branch below (rather than
// after it, as it was originally) so CORS headers still get applied to
// responses the exception handler generates - CORS is the outer wrapper on
// the way back out, so a 4xx/5xx from /api no longer looks like a CORS
// failure to a cross-origin frontend instead of the real error.
app.UseCors(ApiServicesRegistration.ClientAppCorsPolicy);

// Scope global error and status code handling strictly to /api routes
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), apiApp =>
{
    // 1. Handles unmapped 404/405 routes under /api
    apiApp.UseStatusCodePages(async statusCodeContext =>
    {
        HttpResponse response = statusCodeContext.HttpContext.Response;

        if (response.StatusCode == StatusCodes.Status404NotFound)
        {
            PathString path = statusCodeContext.HttpContext.Request.Path;

            await Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not Found",
                detail: $"The requested endpoint '{path}' was not found.",
                instance: path
            ).ExecuteAsync(statusCodeContext.HttpContext);
        }
    });

    // 2. Handles exceptions thrown inside /api request pipelines
    apiApp.UseExceptionHandler(_ => { });
});

// Explicit rather than relying on WebApplication's implicit
// auto-insertion (which only fires right before the first
// endpoint-routing-aware middleware) - UseTickerQ below maps the
// dashboard's own endpoints and needs HttpContext.User already populated.
app.UseAuthentication();
app.UseAuthorization();

app.UseRateLimiter();

app.UseTickerQ();

// Outside the /api scoping above and unauthenticated on purpose - this is
// for a load balancer/orchestrator to poll, not an API consumer, so it
// shouldn't inherit either the ProblemDetails error shaping or any auth
// requirement those routes carry.
app.MapHealthChecks("/health");

app.UseFastEndpoints(options =>
{
    // See ApiJsonTypeInfoResolver - the same combined resolver is also
    // wired onto ASP.NET Core's native Http.Json.JsonOptions in
    // ApiServicesRegistration, so GlobalExceptionHandler's WriteAsJsonAsync
    // and the 404 page's Results.Problem (neither goes through
    // FastEndpoints) get the same source-generated coverage instead of
    // falling back to reflection.
    options.Serializer.Options.TypeInfoResolver = ApiJsonTypeInfoResolver.Combined;

    options.Errors.StatusCode = StatusCodes.Status400BadRequest;

    // FastEndpoints' own FluentValidation failures never throw, so
    // GlobalExceptionHandler never sees them - built here instead.
    // Reshaping into the same ValidationProblemDetails shape means a bad
    // DTO and a thrown ValidationException deep in a handler come back
    // looking identical on the wire.
    options.Errors.ResponseBuilder = (failures, ctx, statusCode) =>
    {
        var errors = failures
            .GroupBy(f => f.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => f.ErrorMessage).ToArray());

        return new ValidationProblemDetails(errors)
        {
            Status = statusCode,
            Title = "Validation failed",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            Instance = ctx.Request.Path
        };
    };
});

app.Run();
