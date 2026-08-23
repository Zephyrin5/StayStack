using Api;
using Api.RateLimiting;
using Api.Serialization;
using Bookings;
using Catalog;
using FastEndpoints;
using FastEndpoints.OpenApi;
using Hosts;
using Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Persistence;
using Scalar.AspNetCore;
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
builder.Services.ConfigureBookingsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureTransactionsServices(builder.Configuration, builder.Environment);
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
});

builder.Services.AddHttpContextAccessor();

builder.Services.OpenApiDocument(o =>
{
    o.DocumentName = "api";
    o.Title = "StayStack API";
    o.Version = "v1";
    o.AutoTagPathSegmentIndex = 0;
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

app.UseRequestLocalization();

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

app.UseHttpsRedirection();

app.UseCors(ApiServicesRegistration.ClientAppCorsPolicy);

app.UseRateLimiter();

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
