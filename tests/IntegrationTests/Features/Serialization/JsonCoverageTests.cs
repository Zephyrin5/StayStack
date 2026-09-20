using Api.Serialization;
using FastEndpoints;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BookingsDiscoveredTypes = Bookings.DiscoveredTypes;
using CatalogDiscoveredTypes = Catalog.DiscoveredTypes;
using HostsDiscoveredTypes = Hosts.DiscoveredTypes;
using IdentityDiscoveredTypes = Identity.DiscoveredTypes;
using PromotionsDiscoveredTypes = Promotions.DiscoveredTypes;
using ReviewsDiscoveredTypes = Reviews.DiscoveredTypes;
using TransactionsDiscoveredTypes = Transactions.DiscoveredTypes;
namespace IntegrationTests.Features.Serialization;

// ApiJsonTypeInfoResolver.Combined has no reflection fallback: a type no module's source-generated
// context covers throws at serialization time, on the request that needed it. Under Native AOT there
// is no fallback to be had at all.
//
// So the coverage is checked here instead, against the endpoints FastEndpoints actually discovered -
// a list generated at build time, so an endpoint added tomorrow is in it without anyone adding a
// line. No host and no database: this asks the resolver a question, nothing more.
public class JsonCoverageTests
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        TypeInfoResolver = ApiJsonTypeInfoResolver.Combined
    };

    private static IEnumerable<Type> DiscoveredEndpoints() =>
        new[]
            {
                IdentityDiscoveredTypes.All,
                CatalogDiscoveredTypes.All,
                HostsDiscoveredTypes.All,
                PromotionsDiscoveredTypes.All,
                BookingsDiscoveredTypes.All,
                ReviewsDiscoveredTypes.All,
                TransactionsDiscoveredTypes.All,
                global::Api.DiscoveredTypes.All
            }
            .SelectMany(types => types)
            .Where(type => type is { IsAbstract: false } && typeof(IEndpoint).IsAssignableFrom(type))
            .Distinct();

    // Endpoint<TRequest, TResponse>, Endpoint<TRequest>, EndpointWithoutRequest<TResponse>: the shape
    // is in the generic arguments of whichever FastEndpoints base the endpoint derives from.
    private static IEnumerable<Type> RequestAndResponseTypes(Type endpoint)
    {
        for (Type? current = endpoint; current is not null; current = current.BaseType)
        {
            if (!current.IsGenericType || current.Namespace?.StartsWith("FastEndpoints", StringComparison.Ordinal) != true)
            {
                continue;
            }

            foreach (Type argument in current.GetGenericArguments())
            {
                // EmptyRequest/EmptyResponse are FastEndpoints' own and never serialized by this app.
                if (argument.Namespace?.StartsWith("FastEndpoints", StringComparison.Ordinal) != true)
                {
                    yield return argument;
                }
            }

            yield break;
        }
    }

    [Fact]
    public void EveryEndpointsRequestAndResponse_ResolvesThroughTheGeneratedContexts()
    {
        List<Type> endpoints = [.. DiscoveredEndpoints()];

        // Not vacuous: the discovered lists are generated, and an empty one would pass silently.
        Assert.True(endpoints.Count >= 30, $"Found only {endpoints.Count} endpoints - the scan is broken.");

        List<string> missing = [];

        foreach (Type endpoint in endpoints)
        {
            foreach (Type shape in RequestAndResponseTypes(endpoint))
            {
                if (ApiJsonTypeInfoResolver.Combined.GetTypeInfo(shape, Options) is null)
                {
                    missing.Add($"{endpoint.Name}: {shape.FullName}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            "These endpoint shapes resolve through no source-generated JSON context, so serializing one " +
            "throws at request time - and under Native AOT there is no reflection fallback to reach for. " +
            "Add each to its module's JsonSerializerContext:\n  " + string.Join("\n  ", missing.Distinct()));
    }

    [Fact]
    public void TheResolver_HasNoReflectionFallback()
    {
        // A type no context covers must resolve to nothing. If a fallback is ever added back, the test
        // above stops meaning anything - it would pass for every type in the process.
        Assert.Null(ApiJsonTypeInfoResolver.Combined.GetTypeInfo(typeof(Assembly), Options));
    }
}
