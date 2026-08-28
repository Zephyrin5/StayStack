using Api.Serialization;
using SeedWork.ValueObjects;
using System.Text.Json;
namespace IntegrationTests.Serialization;

// Money is deliberately domain-only (see docs/adr/0015) - no response DTO
// should ever reference it directly, since ApiJsonTypeInfoResolver has no
// reflection fallback by design (its own doc comment) and an uncovered type
// would only surface as a runtime 500 on whichever endpoint touched it, not
// a compile error. This test is the regression guard: if Money is ever
// accidentally added to a [JsonSerializable] context (directly, or by being
// embedded in a DTO that is), this starts resolving and the test fails
// before it ever reaches production.
public class ApiJsonTypeInfoResolverTests
{
    [Fact]
    public void Combined_ShouldNotResolveMoney_BecauseItIsDomainOnly()
    {
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            TypeInfoResolver = ApiJsonTypeInfoResolver.Combined
        };

        var typeInfo = ApiJsonTypeInfoResolver.Combined.GetTypeInfo(typeof(Money), options);

        Assert.Null(typeInfo);
    }
}
