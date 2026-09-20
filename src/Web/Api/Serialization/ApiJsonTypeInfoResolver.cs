using Bookings.Serialization;
using Catalog.Serialization;
using Hosts.Serialization;
using Identity.Serialization;
using Promotions.Serialization;
using Reviews.Serialization;
using System.Text.Json.Serialization.Metadata;
using Transactions.Serialization;
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
///     silently drifting apart. There is deliberately no reflection fallback:
///     new JSON shapes must be added to a source-generated context.
/// </summary>
public static class ApiJsonTypeInfoResolver
{
    public static readonly IJsonTypeInfoResolver Combined = JsonTypeInfoResolver.Combine(
        IdentityJsonSerializerContext.Default,
        CatalogJsonSerializerContext.Default,
        HostsJsonSerializerContext.Default,
        PromotionsJsonSerializerContext.Default,
        BookingsJsonSerializerContext.Default,
        ReviewsJsonSerializerContext.Default,
        TransactionsJsonSerializerContext.Default,
        AppJsonSerializerContext.Default);
}
