using Api;
using Api.Configuration;
using Api.RateLimiting;
using Api.Security;
using Bookings;
using Bookings.Contracts;
using BuildingBlocks.Configuration;
using BuildingBlocks.Observability;
using Catalog;
using Catalog.Contracts;
using Hosts;
using Identity;
using Jobs;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Persistence;
using Promotions;
using Reviews;
using Scalar.AspNetCore;
using TickerQ.DependencyInjection;
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
    builder.Configuration.GetConnectionString("AppConnection") is { Length: > 0 } appConnection
        ? appConnection
        : throw new InvalidOperationException(
            "Connection string AppConnection is missing or empty. appsettings.json ships it empty on " +
            "purpose, so every environment has to supply its own."),
    builder.Environment.IsDevelopment());
builder.Services.ConfigureApiServices(builder.Configuration);
builder.Services.ConfigureCatalogServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureHostsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigurePromotionsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureBookingsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureReviewsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureTransactionsServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureJobsServices(builder.Configuration, builder.Environment);

// Every rule these types carry - per-field from DataAnnotations, cross-field from the validators in
// StartupChecks - runs at startup, so a misconfiguration is a failure to start rather than a symptom.
// Bound one type at a time: the configuration binding source generator needs the concrete type, and a
// generic helper puts it back on reflection (docs/aot-migration.md).
builder.Services.AddOptions<BookingLifecyclePolicyOptions>()
    .Bind(builder.Configuration.AppSection(BookingLifecyclePolicyOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BookingLifecyclePolicyOptions>, BookingLifecyclePolicyOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<BookingLifecyclePolicyOptions>, BookingLifecycleCrossFieldValidator>();
builder.Services.AddOptions<StaySearchPolicyOptions>()
    .Bind(builder.Configuration.AppSection(StaySearchPolicyOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<StaySearchPolicyOptions>, StaySearchPolicyOptionsValidator>();
builder.Services.AddOptions<AuthRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(AuthRateLimitOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AuthRateLimitOptions>, AuthRateLimitOptionsValidator>();
builder.Services.AddOptions<HoldRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(HoldRateLimitOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<HoldRateLimitOptions>, HoldRateLimitOptionsValidator>();
builder.Services.AddOptions<ReadRateLimitOptions>()
    .Bind(builder.Configuration.AppSection(ReadRateLimitOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ReadRateLimitOptions>, ReadRateLimitOptionsValidator>();
builder.Services.AddOptions<CookieSecurityOptions>()
    .Bind(builder.Configuration.AppSection(CookieSecurityOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<CookieSecurityOptions>, CookieSecurityCrossFieldValidator>();
builder.Services.AddOptions<ForwardedHeadersSettings>()
    .Bind(builder.Configuration.AppSection(ForwardedHeadersSettings.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ForwardedHeadersSettings>, ForwardedHeadersSettingsValidator>();
builder.Services.AddSingleton<IConfigureOptions<ForwardedHeadersOptions>, ConfigureForwardedHeaders>();

builder.Services.AddSingleton(TimeProvider.System);
// Without a registered check, a health endpoint reports Healthy with the database unreachable.
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthCheckTags.Ready]);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddFixedWindow<AuthRateLimitOptions>(ApiServicesRegistration.AuthRateLimitPolicy)
        .AddFixedWindow<HoldRateLimitOptions>(ApiServicesRegistration.HoldRateLimitPolicy)
        .AddFixedWindow<ReadRateLimitOptions>(ApiServicesRegistration.ReadRateLimitPolicy);
});

builder.Services.AddHttpContextAccessor();

builder.Services.AddApiDocumentation();

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    app.MapScalarApiReference("api/docs", options => options
        .WithTitle("StayStack API")
        .WithTheme(ScalarTheme.None)
        .AddDocument("api")
        .WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.HttpClient));
}

app.UseForwardedHeaders();

app.UseRequestLocalization();

app.UseHttpsRedirection();

// Before the /api exception handling, so error responses carry CORS headers.
app.UseCors(ApiServicesRegistration.ClientAppCorsPolicy);

app.UseApiErrors();

// Explicit: the TickerQ dashboard endpoints need HttpContext.User populated.
app.UseAuthentication();
app.UseAuthorization();

app.UseRateLimiter();

app.UseTickerQ();

// Liveness runs no checks, so a database blip never restarts the process; readiness checks dependencies.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready",
    new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthCheckTags.Ready) });

app.UseApiEndpoints();

app.Run();
