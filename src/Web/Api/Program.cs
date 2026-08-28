using Api;
using Api.RateLimiting;
using Api.Serialization;
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

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi

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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHealthChecks();

// Fixed-window, keyed by caller IP - auth and payment-initiation endpoints
// are the obvious credential-stuffing/abuse targets and had no
// application-level limiting at all. RequireRateLimiting("auth") is
// applied per-endpoint via Options() in Configure() (SignInEndpoint,
// RegisterEndpoint, RefreshTokenEndpoint, InitiateTransactionEndpoint).
//
// Limit/window resolved from IOptions<AuthRateLimitOptions> per partition
// (not captured once into a local at startup) specifically so tests can
// override it via the standard services.Configure<AuthRateLimitOptions>
// DI-replacement pattern: appsettings.Testing.json sets a very high limit
// so the shared integration-test WebApplicationFactory (one instance, one
// rate limiter, reused by every test in the collection) doesn't trip it on
// ordinary test traffic; RateLimitingTests overrides it back down on its
// own WithWebHostBuilder-derived factory to actually exercise a 429.
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
    // much damage one hold can do, this bounds how many a single caller can
    // fire. Partitioned the same way as "auth" (by RemoteIpAddress, correct
    // once ForwardedHeaders is processing a real proxy's headers) rather
    // than by the hold-session cookie - the cookie is an ownership handle a
    // scripted caller can drop and regenerate per request, so it would be
    // no partition at all as a rate-limit key.
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
    // concept - there's no first-class option for it here, so it's added
    // via the underlying OpenApiOptions' document-transformer hook instead.
    // Nests Catalog's now-split-out per-family tags (see CatalogGroup.cs
    // and each endpoint's own Description(b => b.WithTags(...)) call)
    // under one collapsible "Catalog" heading in Scalar's sidebar, rather
    // than a flat list of a dozen-plus individually-tagged operations.
    //
    // IMPORTANT: once x-tagGroups is present at all, Scalar stops showing
    // any tag that isn't listed inside SOME group - an ungrouped tag
    // doesn't fall back to its own flat top-level section, it just
    // silently disappears from the sidebar entirely (confirmed by
    // rendering this and diffing against the tag list in the raw document).
    // So every module's tag needs an entry here, even the ones that get no
    // real nesting - a new module/tag added later needs a line added here
    // too, or its docs go dark without any other symptom.
    o.ConfigureOpenApi = openApiOptions =>
    {
        openApiOptions.AddDocumentTransformer((document, _, _) =>
        {
            document.Extensions ??= new Dictionary<string, IOpenApiExtension>();
            document.Extensions["x-tagGroups"] = new JsonNodeExtension(JsonNode.Parse(
                """
                [
                    { "name": "Catalog", "tags": ["Properties", "Units", "Pricing Rules", "Promotions", "Availability"] },
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

// Trusts nothing by default - ForwardedHeadersOptions' own KnownNetworks/
// KnownProxies (loopback only) would otherwise let an untrusted edge spoof
// X-Forwarded-For/-Proto and defeat both AuthCookies' Secure-flag check and
// the IP-partitioned rate limiter's partition key below. Populated from
// config (never hardcoded) so each deployment lists its actual reverse
// proxy/load balancer addresses. Registered before anything else in the
// pipeline reads Request.IsHttps or Connection.RemoteIpAddress.
ForwardedHeadersOptions forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
foreach (string proxy in app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
{
    forwardedHeadersOptions.KnownProxies.Add(IPAddress.Parse(proxy));
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

// Explicit rather than relying on WebApplication's implicit auto-insertion
// (which only fires immediately before the first endpoint-routing-aware
// middleware) - UseTickerQ below maps the dashboard's own endpoints and
// needs HttpContext.User already populated via WithHostAuthentication.
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
    // See ApiJsonTypeInfoResolver - the same combined resolver is also wired
    // onto ASP.NET Core's native Http.Json.JsonOptions in
    // ApiServicesRegistration, so GlobalExceptionHandler's WriteAsJsonAsync
    // and the 404 page's Results.Problem (neither of which goes through
    // FastEndpoints) get the same source-generated coverage instead of
    // silently falling back to full reflection.
    options.Serializer.Options.TypeInfoResolver = ApiJsonTypeInfoResolver.Combined;

    options.Errors.StatusCode = StatusCodes.Status400BadRequest;

    // FastEndpoints' own FluentValidation failures never throw, so
    // GlobalExceptionHandler never sees them - they're built here
    // instead. Reshaping them into the same ValidationProblemDetails
    // shape means a request with a bad DTO and a request that hits a
    // thrown ValidationException deep in a handler come back looking
    // identical on the wire.
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
