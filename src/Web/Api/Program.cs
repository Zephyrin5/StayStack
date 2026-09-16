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

// CreateSlimBuilder does not add user secrets.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(true);
}

builder.Services.ConfigureIdentityServices(builder.Configuration, builder.Environment);
// TODO: disabled until Grafana is configured. Invert PayloadRedactor to an allow-list first: as a
// deny-list it logs every property not marked [Sensitive].
//builder.Services.ConfigureObservabilityServices(builder.Configuration);
builder.Services.ConfigurePersistenceServices();
builder.Services.AddAppDbContext(
    builder.Configuration.GetConnectionString("AppConnection")
    ?? throw new InvalidOperationException("Connection string AppConnection not found."),
    builder.Environment.IsDevelopment());
builder.Services.ConfigureApiServices(builder.Configuration);
builder.Services.ConfigureCatalogServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureHostsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigurePromotionsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureBookingsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureReviewsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureTransactionsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureJobsServices(builder.Configuration, builder.Environment);
builder.Services.AddOptions<BookingLifecyclePolicyOptions>()
    .Bind(builder.Configuration.AppSection(BookingLifecyclePolicyOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BookingLifecyclePolicyOptions>, BookingLifecyclePolicyOptionsValidator>();
builder.Services.AddOptions<StaySearchPolicyOptions>()
    .Bind(builder.Configuration.AppSection(StaySearchPolicyOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<StaySearchPolicyOptions>, StaySearchPolicyOptionsValidator>();

builder.Services.AddSingleton(TimeProvider.System);
// Without a registered check, a health endpoint reports Healthy with the database unreachable.
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthCheckTags.Ready]);

builder.Services.AddOptions<CookieSecurityOptions>()
    .Bind(builder.Configuration.AppSection(CookieSecurityOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddOptions<AuthRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(AuthRateLimitOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AuthRateLimitOptions>, AuthRateLimitOptionsValidator>();
builder.Services.AddOptions<HoldRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(HoldRateLimitOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<HoldRateLimitOptions>, HoldRateLimitOptionsValidator>();
builder.Services.AddOptions<ReadRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(ReadRateLimitOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ReadRateLimitOptions>, ReadRateLimitOptionsValidator>();

// Fixed-window limiters keyed by caller address; limits are read per partition so tests can override them.
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

    // Not keyed by anything the client supplies, which a scripted caller could regenerate per request.
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

    // Anonymous reads. Deliberately loose: everyone behind one NAT shares the budget.
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

    // Scalar hides any tag not listed in a group, so a new tag needs a line here.
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

// Only loopback is trusted by default. Behind any other proxy, unlisted, every caller shares one
// rate-limit partition and one hold cap.
ForwardedHeadersOptions forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};

// Counted from configuration: the options object is pre-seeded with loopback entries.
string[] configuredProxies = app.Configuration.AppSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
string[] configuredNetworks = app.Configuration.AppSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];

foreach (string proxy in configuredProxies)
{
    forwardedHeadersOptions.KnownProxies.Add(IPAddress.Parse(proxy));
}

// CIDR ranges, for managed load balancers with no listable address.
foreach (string network in configuredNetworks)
{
    forwardedHeadersOptions.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
}

// A guest's management token must outlive the review window, or guests cannot reach their booking to review.
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

// Browsers reject SameSite=None cookies that are not Secure, so cookie auth could never work.
CookieSecurityOptions cookieSecurity =
    app.Services.GetRequiredService<IOptions<CookieSecurityOptions>>().Value;
if (cookieSecurity.SameSite == SameSiteMode.None && !cookieSecurity.RequireSecure)
{
    throw new InvalidOperationException(
        $"{CookieSecurityOptions.SectionName}:SameSite is None but RequireSecure is false. Browsers reject " +
        "SameSite=None cookies that are not Secure, so no session cookie would ever be stored. A cross-site " +
        "SPA needs both; a same-site one should leave SameSite at Lax.");
}

// A cross-site CORS origin with SameSite=Lax: the browser never attaches the cookie, and auth fails silently.
string[] corsOrigins = app.Configuration.AppSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

if (corsOrigins.Length > 0 && cookieSecurity.SameSite == SameSiteMode.Lax)
{
    if (string.IsNullOrWhiteSpace(cookieSecurity.ApiOrigin))
    {
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

// Outside Development, someone must list the proxies or declare there are none (ExposedDirectly).
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

// Before the /api exception handling, so error responses carry CORS headers.
app.UseCors(ApiServicesRegistration.ClientAppCorsPolicy);

app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), apiApp =>
{
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

    apiApp.UseExceptionHandler(_ => { });
});

// Explicit: the TickerQ dashboard endpoints need HttpContext.User populated.
app.UseAuthentication();
app.UseAuthorization();

app.UseRateLimiter();

app.UseTickerQ();

// Liveness runs no checks, so a database blip never restarts the process; readiness checks dependencies.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready",
    new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthCheckTags.Ready) });

app.UseFastEndpoints(options =>
{
    // Also set on Http.Json.JsonOptions (ApiServicesRegistration) for responses outside FastEndpoints.
    options.Serializer.Options.TypeInfoResolver = ApiJsonTypeInfoResolver.Combined;

    options.Errors.StatusCode = StatusCodes.Status400BadRequest;

    // Same shape as a thrown ValidationException, which GlobalExceptionHandler writes.
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
