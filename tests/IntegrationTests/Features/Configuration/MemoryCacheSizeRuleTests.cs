using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;
namespace IntegrationTests.Features.Configuration;

// The executable half of docs/adr/0024. Setting SizeLimit on the shared
// IMemoryCache turns "forgot to set Size" from a sizing mistake into an
// exception on the write path - and the constraint lives in one
// AddMemoryCache line that nothing else in the codebase points at, so the only
// way to learn it has been to hit it.
[Collection("Integration Tests")]
public class MemoryCacheSizeRuleTests(IntegrationTestWebApplicationFactory factory)
{
    private IMemoryCache Cache => factory.Services.GetRequiredService<IMemoryCache>();

    [Fact]
    public void TheSharedCacheIsBounded()
    {
        // If this ever becomes null the rule below stops applying, every entry
        // silently starts being accepted without a Size, and the L1 becomes
        // unbounded - which is the failure this pair of tests exists to keep
        // visible in both directions.
        MemoryCacheOptions options = factory.Services
            .GetRequiredService<IOptions<MemoryCacheOptions>>().Value;

        Assert.NotNull(options.SizeLimit);
        Assert.True(options.SizeLimit > 0);
    }

    [Fact]
    public void AnEntryWithoutASize_Throws()
    {
        // The rule itself, pinned rather than described. A component that adds
        // a cache entry the ordinary way does not fail at startup, or in
        // review, or under a smoke test that misses the cache path - it throws
        // on the first request that populates the entry.
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Cache.Set($"size-rule-{Guid.NewGuid()}", "value"));

        Assert.Contains("Size", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEntryWithASize_IsAccepted()
    {
        // The other half, so the rule cannot be "satisfied" by a cache that
        // rejects everything.
        string key = $"size-rule-{Guid.NewGuid()}";

        Cache.Set(key, "value", new MemoryCacheEntryOptions { Size = 1 });

        Assert.True(Cache.TryGetValue(key, out string? cached));
        Assert.Equal("value", cached);
    }

    [Fact]
    public async Task HybridCache_CompliesWithTheRule()
    {
        // The one component that uses this cache today. It sets each L1 entry's
        // Size to the serialized payload length, which is what makes the byte
        // budget meaningful rather than a count of entries - and is why the
        // limit is expressed in bytes.
        HybridCache hybrid = factory.Services.GetRequiredService<HybridCache>();

        string value = await hybrid.GetOrCreateAsync(
            $"size-rule-{Guid.NewGuid()}",
            _ => ValueTask.FromResult("cached"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("cached", value);
    }

    [Fact]
    public void NothingInjectsTheSharedCacheDirectly()
    {
        // The structural guard, and the reason it is a test rather than a
        // paragraph: whoever is about to break this rule is by definition
        // someone who has not read the rule. Failing here puts the constraint
        // in front of them at the moment it becomes their problem.
        //
        // Not a ban. Taking an IMemoryCache dependency is allowed - it just
        // has to be a decision, made knowing that every Set needs a Size and
        // that the 64 MB budget is shared with HybridCache rather than being
        // yours. Adding a type here is the way to record that decision.
        string[] permitted = [];

        List<string> offenders =
        [
            .. from assembly in ApplicationAssemblies()
               from type in assembly.GetTypes()
               from constructor in type.GetConstructors(
                   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
               where constructor.GetParameters().Any(p => p.ParameterType == typeof(IMemoryCache))
               where !permitted.Contains(type.FullName ?? type.Name)
               select type.FullName ?? type.Name
        ];

        Assert.True(
            offenders.Count == 0,
            "These types take IMemoryCache directly: " + string.Join(", ", offenders.Distinct()) +
            ". The shared cache has a SizeLimit, so every entry MUST specify a Size or the Set throws at " +
            "runtime - see docs/adr/0024. Once you have handled that, add the type to the permitted list here.");
    }

    // Our own assemblies only: the framework and third-party libraries take
    // IMemoryCache all over the place, and none of that is ours to govern.
    private static IEnumerable<Assembly> ApplicationAssemblies()
    {
        string[] ours =
        [
            "Api", "BuildingBlocks", "SeedWork", "Persistence", "Jobs",
            "Identity", "Catalog", "Hosts", "Promotions", "Bookings", "Reviews", "Transactions"
        ];

        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => ours.Contains(assembly.GetName().Name));
    }
}
