using BuildingBlocks.Localization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
namespace IntegrationTests.Features.Localization;

// LocalizationSettings is injected as IOptions<LocalizationSettings> by every
// create/update handler that builds a LocalizedText - it is the platform-wide
// answer to "which language is required".
//
// IOptions<T> resolves whether or not anything ever configured T: unbound, it
// hands back a default-constructed instance. That makes a missing registration
// invisible when the type's own defaults happen to match appsettings, which is
// exactly the situation this pins - the values agreed, so nothing looked wrong,
// and the configuration section was not actually being read.
[Collection(CommonCollection.Name)]
public class LocalizationSettingsBindingTests(CommonFixture factory)
{
    [Fact]
    public void LocalizationSettings_ReflectsConfiguration_NotJustItsOwnDefaults()
    {
        // Deliberately different from the type's defaults ("en" / en+ar), so
        // this can only pass if the section is genuinely bound.
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection([
                new KeyValuePair<string, string?>("App:Localization:DefaultCulture", "ar"),
                new KeyValuePair<string, string?>("App:Localization:SupportedCultures:0", "ar"),
                new KeyValuePair<string, string?>("App:Localization:SupportedCultures:1", "fr")
            ])));

        using IServiceScope scope = host.Services.CreateScope();
        LocalizationSettings settings =
            scope.ServiceProvider.GetRequiredService<IOptions<LocalizationSettings>>().Value;

        Assert.Equal("ar", settings.DefaultCulture);
        // Exactly the configured list. With a default initializer on the
        // property this came back as ["en", "ar", "ar", "fr"] - the binder
        // appends to a non-empty collection rather than replacing it, so the
        // defaults could never be removed and duplicated when overlapped.
        Assert.Equal(["ar", "fr"], settings.SupportedCultures);
    }

    [Fact]
    public void LocalizationSettings_MatchesTheCultureTheRequestPipelineUses()
    {
        // One source feeds both IOptions<LocalizationSettings> and
        // RequestLocalizationOptions, so a configured default culture cannot
        // mean one thing to a LocalizedText and another to culture negotiation.
        using IServiceScope scope = factory.Services.CreateScope();

        LocalizationSettings settings =
            scope.ServiceProvider.GetRequiredService<IOptions<LocalizationSettings>>().Value;
        IConfiguration configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        Assert.Equal(configuration["App:Localization:DefaultCulture"], settings.DefaultCulture);
    }
}
