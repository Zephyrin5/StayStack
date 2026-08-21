using Api;
using Catalog;
using Catalog.Serialization;
using FastEndpoints;
using FastEndpoints.OpenApi;
using Hosts;
using Hosts.Serialization;
using Identity;
using Identity.Serialization;
using Microsoft.AspNetCore.Mvc;
using Persistence;
using Scalar.AspNetCore;
using System.Text.Json.Serialization.Metadata;
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseKestrelHttpsConfiguration();

// CreateSlimBuilder, unlike CreateBuilder, doesn't wire up user-secrets by
// default - added explicitly so local connection strings/keys can live in
// the Secret Manager instead of appsettings.json.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi

builder.Services.ConfigureIdentityServices(builder.Configuration, builder.Environment);
// TODO: Disabled until Grafana is configured
//builder.Services.ConfigureObservabilityServices(builder.Configuration);
builder.Services.ConfigurePersistenceServices(builder.Configuration);
builder.Services.ConfigureApiServices(builder.Configuration);
builder.Services.ConfigureCatalogServices(builder.Configuration, builder.Environment);
builder.Services.ConfigureHostsServices(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHealthChecks();

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

// Outside the /api scoping above and unauthenticated on purpose - this is
// for a load balancer/orchestrator to poll, not an API consumer, so it
// shouldn't inherit either the ProblemDetails error shaping or any auth
// requirement those routes carry.
app.MapHealthChecks("/health");

app.UseFastEndpoints(options =>
{
    // Each module owns the source-generated context for its own Request/
    // Response DTOs (IdentityJsonSerializerContext, CatalogFeaturesJsonSerializerContext,
    // HostsFeaturesJsonSerializerContext); this combines them into the one
    // resolver FastEndpoints actually uses for (de)serialization. The
    // DefaultJsonTypeInfoResolver at the end is a reflection fallback for
    // anything none of those cover - framework types like ProblemDetails/
    // ValidationProblemDetails that FastEndpoints' own validation-failure
    // responses build (see Errors.ResponseBuilder below) - so an
    // unregistered type fails closed to reflection instead of throwing.
    options.Serializer.Options.TypeInfoResolver = JsonTypeInfoResolver.Combine(
        IdentityJsonSerializerContext.Default,
        CatalogJsonSerializerContext.Default,
        HostsFeaturesJsonSerializerContext.Default,
        AppJsonSerializerContext.Default,
        new DefaultJsonTypeInfoResolver());

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
