using Api.Serialization;
using FastEndpoints;
using FastEndpoints.OpenApi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;
namespace Api.Configuration;

/// <summary>
///     The parts of the pipeline with shape of their own - documentation, error bodies, endpoint
///     serialization - so Program.cs reads as the order they run in.
/// </summary>
internal static class ApiPipeline
{
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services) =>
        services.OpenApiDocument(o =>
        {
            o.DocumentName = "api";
            o.Title = "StayStack API";
            o.Version = "v1";
            o.AutoTagPathSegmentIndex = 0;

            // Scalar hides any tag not listed in a group, so a new tag needs a line here.
            o.ConfigureOpenApi = openApiOptions => openApiOptions.AddDocumentTransformer((document, _, _) =>
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
        });

    /// <summary>
    ///     Problem+json for both ways an /api request can fail without reaching a handler: a route
    ///     that matched nothing, and an exception on one that did.
    /// </summary>
    public static IApplicationBuilder UseApiErrors(this WebApplication app) =>
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), apiApp =>
        {
            apiApp.UseStatusCodePages(async statusCodeContext =>
            {
                if (statusCodeContext.HttpContext.Response.StatusCode != StatusCodes.Status404NotFound)
                {
                    return;
                }

                PathString path = statusCodeContext.HttpContext.Request.Path;

                await Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Not Found",
                    detail: $"The requested endpoint '{path}' was not found.",
                    instance: path).ExecuteAsync(statusCodeContext.HttpContext);
            });

            apiApp.UseExceptionHandler(_ => { });
        });

    public static IApplicationBuilder UseApiEndpoints(this WebApplication app) =>
        app.UseFastEndpoints(options =>
        {
            // Also set on Http.Json.JsonOptions (ApiServicesRegistration) for responses outside FastEndpoints.
            options.Serializer.Options.TypeInfoResolver = ApiJsonTypeInfoResolver.Combined;

            options.Errors.StatusCode = StatusCodes.Status400BadRequest;

            // Same shape as a thrown ValidationException, which GlobalExceptionHandler writes.
            options.Errors.ResponseBuilder = (failures, ctx, statusCode) => new ValidationProblemDetails(
                failures.GroupBy(f => f.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray()))
            {
                Status = statusCode,
                Title = "Validation failed",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                Instance = ctx.Request.Path
            };
        });
}
