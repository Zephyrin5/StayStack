using Api.RateLimiting;
using Availability.Features.HoldAvailability;
using Bookings.Contracts;
using BuildingBlocks.Localization;
using Identity.Configurations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
namespace IntegrationTests.Features.Configuration;

// Every options type was registered with a bare Configure<T>(section), which
// binds whatever is there and validates nothing. So values that are individually
// nonsensical - a negative review window, a hold cap of zero, an empty signing
// key - bound cleanly and the app started looking healthy, failing later as
// behaviour rather than at boot as configuration.
//
// The cross-field invariants already had bespoke startup checks. These cover
// the per-field ones the bespoke checks were never going to catch, and assert
// startup refuses rather than that the value merely round-trips.
[Collection("Integration Tests")]
public class OptionsValidationTests(IntegrationTestWebApplicationFactory factory)
{
    private WebApplicationFactory<Program> HostWith(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                [.. settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))])));

    // Asserts the specific validation failure, not merely that startup broke -
    // a bare ThrowsAny would pass just as happily on a connection string typo,
    // which would make these tests look like they were doing their job while
    // proving nothing about validation.
    private void AssertRefusesToStart(string expectedMember, params (string Key, string Value)[] settings)
    {
        using WebApplicationFactory<Program> host = HostWith(settings);

        // ValidateOnStart runs during host start, which WebApplicationFactory
        // triggers on first client creation.
        OptionsValidationException exception =
            Assert.Throws<OptionsValidationException>(() => host.CreateClient());

        Assert.Contains(expectedMember, string.Join(" ", exception.Failures));
    }

    [Fact]
    public void ANegativeReviewWindow_RefusesToStart()
    {
        // -1 would close reviews the day before checkout, so the endpoint
        // rejects every review while reporting nothing wrong.
        AssertRefusesToStart(
            "ReviewWindowDaysAfterCheckOut",
            ("App:BookingLifecycle:ReviewWindowDaysAfterCheckOut", "-1"));
    }

    [Fact]
    public void AZeroHoldCap_RefusesToStart()
    {
        // 0 rejects every hold with 429 - indistinguishable from an outage,
        // and reachable by one keystroke.
        AssertRefusesToStart(
            "MaxActiveHoldsPerClient",
            ("App:Holds:MaxActiveHoldsPerClient", "0"));
    }

    [Fact]
    public void AZeroRateLimit_RefusesToStart()
    {
        AssertRefusesToStart(
            "AuthPermitLimit",
            ("App:RateLimiting:AuthPermitLimit", "0"));
    }

    [Fact]
    public void AnEmptySigningKey_RefusesToStart()
    {
        AssertRefusesToStart(
            "Key",
            ("App:Auth:Token:Key", ""));
    }

    [Fact]
    public void ASigningKeyTooShortForHmacSha256_RefusesToStart()
    {
        // Non-empty, so Required is satisfied and this used to boot - then
        // fail the first sign-in, because SymmetricSecurityKey's constructor
        // only rejects a zero-length key and the 128-bit floor is enforced
        // later, when SymmetricSignatureProvider signs a token. An auth
        // outage on first use, from a value that looked configured.
        AssertRefusesToStart(
            "Key",
            ("App:Auth:Token:Key", "too-short-for-hmac"));
    }

    // The two below go through Configure<T> rather than AssertRefusesToStart's
    // config keys. Configuration merges array elements by key, so layering an
    // in-memory provider over appsettings.json can replace
    // SupportedCultures:0 or append :2, but cannot clear the ["en", "ar"] it
    // already declares - and an unconfigured list is the case worth rejecting.
    // Configure<T> replaces the bound value outright, and still runs before
    // ValidateOnStart, so it exercises the same validation the real startup does.
    private void AssertRefusesToStart(string expectedMessageFragment, Action<LocalizationSettings> configure)
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Configure(configure)));

        OptionsValidationException exception =
            Assert.Throws<OptionsValidationException>(() => host.CreateClient());

        Assert.Contains(expectedMessageFragment, string.Join(" ", exception.Failures));
    }

    [Fact]
    public void AnEmptySupportedCulturesList_RefusesToStart()
    {
        // The bilingual requirement is a product guarantee. This used to bind
        // cleanly and fall through to a hardcoded ["en", "ar"] in
        // ApiServicesRegistration, so a cleared section still served two
        // languages and nothing anywhere said the config had stopped being
        // read.
        AssertRefusesToStart(
            nameof(LocalizationSettings.SupportedCultures),
            o => o.SupportedCultures = []);
    }

    [Fact]
    public void ADefaultCultureOutsideSupportedCultures_RefusesToStart()
    {
        // Each field is individually valid here - non-empty default,
        // non-empty list - so only a cross-field check catches it. Left
        // alone, SetDefaultCulture names a culture AddSupportedCultures was
        // never given, and every request that doesn't negotiate its own
        // culture falls back to one the deployment never declared.
        AssertRefusesToStart(
            "is not one of SupportedCultures",
            o =>
            {
                o.DefaultCulture = "fr";
                o.SupportedCultures = ["en", "ar"];
            });
    }

    [Fact]
    public void ADefaultCultureListedInADifferentCase_StartsNormally()
    {
        // Culture names are case-insensitive to .NET, so this is a spelling
        // difference rather than a misconfiguration - the check must not
        // reject it.
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Configure<LocalizationSettings>(o =>
            {
                o.DefaultCulture = "EN";
                o.SupportedCultures = ["en", "ar"];
            })));

        using IServiceScope scope = host.Services.CreateScope();

        Assert.Equal("EN", scope.ServiceProvider
            .GetRequiredService<IOptions<LocalizationSettings>>().Value.DefaultCulture);
    }

    [Fact]
    public void ValidConfiguration_StartsAndBindsTheConfiguredValues()
    {
        // The other half: validation must not reject values that are merely
        // unusual. A test that only proves things fail would pass against an
        // options type that rejected everything.
        using WebApplicationFactory<Program> host = HostWith(
            ("App:BookingLifecycle:ReviewWindowDaysAfterCheckOut", "30"),
            ("App:BookingLifecycle:ManagementTokenLifetimeDaysAfterCheckOut", "45"),
            ("App:Holds:MaxActiveHoldsPerClient", "3"),
            ("App:RateLimiting:AuthPermitLimit", "7"));

        using IServiceScope scope = host.Services.CreateScope();

        Assert.Equal(30, scope.ServiceProvider
            .GetRequiredService<IOptions<BookingLifecyclePolicyOptions>>().Value.ReviewWindowDaysAfterCheckOut);
        Assert.Equal(3, scope.ServiceProvider
            .GetRequiredService<IOptions<HoldCapOptions>>().Value.MaxActiveHoldsPerClient);
        Assert.Equal(7, scope.ServiceProvider
            .GetRequiredService<IOptions<AuthRateLimitOptions>>().Value.AuthPermitLimit);
        Assert.NotEmpty(scope.ServiceProvider
            .GetRequiredService<IOptions<AuthTokenConfiguration>>().Value.Key);
    }
}
