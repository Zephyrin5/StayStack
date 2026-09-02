using Api.RateLimiting;
using Availability.Features.HoldAvailability;
using Bookings.Contracts;
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
