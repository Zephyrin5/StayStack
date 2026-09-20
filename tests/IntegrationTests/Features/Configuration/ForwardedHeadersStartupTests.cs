using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
namespace IntegrationTests.Features.Configuration;

// Behind a proxy at any non-loopback address, an empty KnownProxies means
// X-Forwarded-For is dropped and RemoteIpAddress is the proxy's own address
// for everyone. Every rate-limit partition then collapses into one - including
// the four anonymous read endpoints, where the result is 429s for every
// visitor at once and an outage with nothing wrong in any log.
//
// So startup refuses rather than warns. The check reads configuration:
// ForwardedHeadersOptions seeds KnownProxies with ::1, so
// `KnownProxies.Count == 0` is false even on a completely unconfigured app.
[Collection("Integration Tests")]
public class ForwardedHeadersStartupTests(IntegrationTestWebApplicationFactory factory)
{
    private WebApplicationFactory<Program> HostWith(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                [.. settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))])));

    // appsettings.Testing.json declares ExposedDirectly, which is true of
    // TestServer - so a test about the unconfigured case has to take it back
    // off rather than assume its absence.
    private const string NoDeclaration = "App:ForwardedHeaders:ExposedDirectly";

    [Fact]
    public void NoProxyDeclarationAtAll_RefusesToStart()
    {
        using WebApplicationFactory<Program> host = HostWith((NoDeclaration, "false"));

        // OptionsValidationException, not InvalidOperationException: the rule is an IValidateOptions
        // checked by ValidateOnStart, so the host refuses to start the way every other invalid setting does.
        OptionsValidationException exception =
            Assert.Throws<OptionsValidationException>(() => host.CreateClient());

        Assert.Contains("KnownProxies", exception.Message);
    }

    [Fact]
    public void AListedProxy_Starts()
    {
        using WebApplicationFactory<Program> host = HostWith(
            (NoDeclaration, "false"),
            ("App:ForwardedHeaders:KnownProxies:0", "10.0.0.7"));

        Assert.NotNull(host.CreateClient());
    }

    [Fact]
    public void ACidrRange_Starts()
    {
        // The case that decides whether this check is workable at all. A
        // managed load balancer has no listable address - AWS and GCP front
        // ends move within a published CIDR - so without this, every cloud
        // deployment's only honest answer would be the escape hatch, and the
        // check would prove nothing.
        using WebApplicationFactory<Program> host = HostWith(
            (NoDeclaration, "false"),
            ("App:ForwardedHeaders:KnownNetworks:0", "10.0.0.0/8"));

        Assert.NotNull(host.CreateClient());
    }

    [Fact]
    public void ADeploymentThatDeclaresItHasNoProxy_Starts()
    {
        // An app terminating its own TLS is a legitimate deployment and must
        // still boot. The point of the pair of settings is not that a proxy
        // exists, it is that somebody answered the question.
        using WebApplicationFactory<Program> host = HostWith((NoDeclaration, "true"));

        Assert.NotNull(host.CreateClient());
    }

    [Fact]
    public void AMalformedCidr_RefusesToStart()
    {
        // Loudly, rather than by silently trusting nothing - a range that does
        // not parse would otherwise leave the deployment believing it had
        // configured one.
        using WebApplicationFactory<Program> host = HostWith(
            (NoDeclaration, "false"),
            ("App:ForwardedHeaders:KnownNetworks:0", "not-a-cidr"));

        Assert.ThrowsAny<Exception>(() => host.CreateClient());
    }
}
