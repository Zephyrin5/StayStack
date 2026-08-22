using Bookings.Serialization;
using Catalog.Serialization;
using Hosts.Serialization;
using Identity.Serialization;
using System.Text.Json.Serialization.Metadata;
namespace Api.Serialization;

/// <summary>
///     The one combined resolver every JSON-writing path in the app shares -
///     FastEndpoints' own request/response pipeline (wired in Program.cs's
///     UseFastEndpoints call) and ASP.NET Core's native
///     Microsoft.AspNetCore.Http.Json.JsonOptions (wired in
///     ApiServicesRegistration, which is what WriteAsJsonAsync in
///     GlobalExceptionHandler and Results.Problem for the 404 page actually
///     use). Kept as a single instance so a type added to one module's
///     context is immediately covered everywhere, instead of the two paths
///     silently drifting apart. The trailing DefaultJsonTypeInfoResolver is
///     a reflection fallback for anything none of the source-generated
///     contexts cover.
/// </summary>
public static class ApiJsonTypeInfoResolver
{
    public static readonly IJsonTypeInfoResolver Combined = JsonTypeInfoResolver.Combine(
        IdentityJsonSerializerContext.Default,
        CatalogJsonSerializerContext.Default,
        HostsFeaturesJsonSerializerContext.Default,
        BookingsJsonSerializerContext.Default,
        AppJsonSerializerContext.Default,
        new DefaultJsonTypeInfoResolver());
}
