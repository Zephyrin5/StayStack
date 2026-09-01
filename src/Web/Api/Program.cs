using Api;
using Api.RateLimiting;
using Api.Security;
using Api.Serialization;
using Availability;
using Bookings;
using Catalog;
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
builder.Services.ConfigureAvailabilityServices(builder.Configuration, builder.Environment);
builder.Services.ConfigurePromotionsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureBookingsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureReviewsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureTransactionsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureJobsServices(builder.Configuration, builder.Environment);
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
builder.Services.Configure<CookieSecurityOptions>(
    builder.Configuration.GetSection(CookieSecurityOptions.SectionName));
builder.Services.Configure<AuthRateLimitOptions>(builder.Configuration.GetSection("RateLimiting"));
// Same "RateLimiting" section, sibling keys - HoldPermitLimit/HoldWindowSeconds
// coexist with AuthPermitLimit/AuthWindowSeconds without colliding.
builder.Services.Configure<HoldRateLimitOptions>(builder.Configuration.GetSection("RateLimiting"));

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
                PermitLimit = limits.AuthPermitLimit,
                Window = TimeSpan.FromSeconds(limits.AuthWindowSeconds),
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
                PermitLimit = limits.HoldPermitLimit,
                Window = TimeSpan.FromSeconds(limits.HoldWindowSeconds),
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
foreach (string proxy in app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
{
    forwardedHeadersOptions.KnownProxies.Add(IPAddress.Parse(proxy));
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

if (!app.Environment.IsDevelopment() && forwardedHeadersOptions.KnownProxies.Count == 0)
{
    // A warning, not a throw: an app exposed directly with its own TLS has
    // no proxy to list, and that is a legitimate deployment. But it is far
    // more often an oversight, and the symptom - every caller sharing one
    // rate-limit and hold-cap partition - reads as mysterious 429s rather
    // than as a configuration problem, so it is worth saying plainly once
    // at startup.
    app.Logger.LogWarning(
        "ForwardedHeaders:KnownProxies is empty outside Development. Only loopback proxies are trusted, " +
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
