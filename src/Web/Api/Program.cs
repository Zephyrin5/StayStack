using Api;
using Api.Configuration;
using Api.RateLimiting;
using Api.Security;
using Api.Serialization;
using Bookings;
using Bookings.Contracts;
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
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using BuildingBlocks.Observability;
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
// TODO: Disabled until Grafana is configured.
//
// Before uncommenting: invert PayloadRedactor to an allow-list. It is a
// deny-list today, so every public property of every request and response is
// serialised verbatim into an Activity tag and into Information/Warning logs
// unless somebody remembered [Sensitive]. Nothing leaks while this line is
// commented out, which is exactly why the cost of a missing annotation is
// invisible until the day it is paid - all at once, retroactively, across
// whatever the log sink retains.
//
// A deny-list also cannot be audited. Reviewing what *is* annotated tells you
// nothing; you would have to review every property that is not, in every
// module, forever. An allow-list makes the same review finite and makes the
// default outcome of forgetting "this field is missing from the trace" rather
// than "this field is in the logs".
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
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BookingLifecyclePolicyOptions>, BookingLifecyclePolicyOptionsValidator>();
// How far ahead a stay can start and how long it can run - read by the search
// path (Catalog) and the hold path (Availability) alike, so the two cannot
// disagree about what is bookable. No consistency check after Build() to pair
// with this one: there is a single value per bound rather than two that have
// to be kept in step. See StaySearchPolicyOptions.
builder.Services.AddOptions<StaySearchPolicyOptions>()
    .Bind(builder.Configuration.AppSection(StaySearchPolicyOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<StaySearchPolicyOptions>, StaySearchPolicyOptionsValidator>();

builder.Services.AddSingleton(TimeProvider.System);
// A bare AddHealthChecks() registers nothing, and an endpoint with no checks
// reports Healthy unconditionally - so /health answered "yes" with the
// database unreachable, which is worse than having no probe at all: an
// orchestrator keeps routing traffic to a node that cannot serve a single
// request, and a deploy that cannot reach its database rolls out green.
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthCheckTags.Ready]);

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
    .ValidateOnStart();
builder.Services.AddOptions<AuthRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(AuthRateLimitOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AuthRateLimitOptions>, AuthRateLimitOptionsValidator>();
// Each policy binds its own nested section under RateLimiting rather than
// sharing one and prefixing property names to stay out of each other's way -
// see AuthRateLimitOptions.SectionName.
builder.Services.AddOptions<HoldRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(HoldRateLimitOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<HoldRateLimitOptions>, HoldRateLimitOptionsValidator>();
builder.Services.AddOptions<ReadRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(ReadRateLimitOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ReadRateLimitOptions>, ReadRateLimitOptionsValidator>();

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
    // Same IP partition as the other two. The limit is deliberately loose
    // because everyone behind one NAT shares a budget and tripping it breaks
    // browsing for people who have done nothing wrong - not, any longer,
    // because the partition might have collapsed to a single bucket: the
    // startup check further down refuses that configuration. See
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
// ForwardedHeadersOptions ships with KnownProxies = { ::1 } and
// KnownNetworks = { 127.0.0.0/8 }, and the loop below only adds to them: with
// the shipped empty config, a loopback caller is trusted and nothing else is.
//
// So ForwardedHeaders:KnownProxies must be populated in any proxied
// deployment. Otherwise a TLS-terminating proxy at a non-loopback address has
// its headers dropped, RemoteIpAddress becomes the proxy's own address, and
// the "holds"/"auth" rate-limit partitions and HoldAvailabilityHandler's
// concurrent-hold cap collapse into one bucket for every caller. The cookie
// Secure flag is configured (CookieSecurityOptions) so it does not depend on
// this. The startup check below makes the misconfiguration loud.
ForwardedHeadersOptions forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};

// Counted from configuration, not from the options object. ForwardedHeaders-
// Options seeds KnownProxies with ::1 and KnownIPNetworks with 127.0.0.0/8, so
// `KnownProxies.Count == 0` is false on a completely unconfigured app - the
// case worth catching.
string[] configuredProxies = app.Configuration.AppSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
string[] configuredNetworks = app.Configuration.AppSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];

foreach (string proxy in configuredProxies)
{
    forwardedHeadersOptions.KnownProxies.Add(IPAddress.Parse(proxy));
}

// Networks as well as addresses, because a managed load balancer does not
// have a listable address. AWS/GCP front ends move within a published CIDR,
// so a deployment there can only ever answer this question with a range - and
// without somewhere to put one, the check below would push every cloud
// deployment straight to the escape hatch and prove nothing.
foreach (string network in configuredNetworks)
{
    // Fully qualified: Microsoft.AspNetCore.HttpOverrides also defines an
    // obsolete IPNetwork.
    forwardedHeadersOptions.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
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

// A throw, because of blast radius. Four anonymous read endpoints -
// GetProperties, GetPropertyById, GetPriceCalendar, GetPropertyReviews - are
// rate-limited per caller address alongside the auth/hold limiters and the
// concurrent-hold cap. Collapsed into one partition, every visitor shares one
// 300-per-minute budget, so the deployment serves 429s to everyone and reads as
// an outage with nothing wrong in any log.
//
// ExposedDirectly is the escape hatch, and it is the reason this can be a
// throw at all. An app terminating its own TLS has no proxy to list, which is
// a legitimate deployment that must still be able to start. What the pair of
// settings buys is that somebody chose - either these are the proxies, or
// there are none.
bool exposedDirectly = app.Configuration.AppSection("ForwardedHeaders:ExposedDirectly").Get<bool>();

if (!app.Environment.IsDevelopment()
    && configuredProxies.Length == 0
    && configuredNetworks.Length == 0
    && !exposedDirectly)
{
    throw new InvalidOperationException(
        "App:ForwardedHeaders:KnownProxies and :KnownNetworks are both empty outside Development. Only " +
        "loopback is trusted, so behind a proxy at any other address X-Forwarded-For/-Proto are dropped and " +
        "every caller shares one rate-limit partition keyed on the proxy's address - including the anonymous " +
        "read endpoints, which would serve 429s to every visitor at once. List the proxy addresses, or a CIDR " +
        "range for a managed load balancer, or set App:ForwardedHeaders:ExposedDirectly to true if this app " +
        "really does terminate its own TLS with nothing in front of it.");
}

app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseRequestLocalization();

app.UseHttpsRedirection();

// Before the /api exception-handler branch, so CORS headers are applied to
// responses the exception handler generates; otherwise a 4xx/5xx from /api
// looks like a CORS failure to a cross-origin frontend.
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

// Outside the /api scoping above and unauthenticated on purpose - these are
// for a load balancer/orchestrator to poll, not an API consumer, so they
// shouldn't inherit either the ProblemDetails error shaping or any auth
// requirement those routes carry.
//
// Split in two because the two questions have opposite remedies. Liveness
// asks "is this process wedged", and the answer to no is to restart the
// container. Readiness asks "can this node serve a request", and the answer
// to no is to stop routing to it until it can. Pointing a liveness probe at a
// dependency check is the classic way to turn a database blip into a
// cluster-wide restart storm, so liveness deliberately runs no checks at all:
// reaching this handler is itself the proof that the process is up and
// serving.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready",
    new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthCheckTags.Ready) });

// Kept, and mapped to readiness rather than removed. It is the address in the
// README and in whatever external monitor someone has already pointed at it,
// and "can it serve" is the question a human typing /health means. Nothing in
// this repo polls it as a liveness probe - if something outside does, it
// wants /health/live now.
app.MapHealthChecks("/health",
    new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthCheckTags.Ready) });

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
