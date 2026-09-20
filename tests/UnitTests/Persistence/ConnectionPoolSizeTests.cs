using Microsoft.Extensions.DependencyInjection;
using Persistence;
namespace UnitTests.Persistence;

// Npgsql pools 100 connections per instance when nothing says otherwise, so two instances already
// exceed a typical max_connections and the failure arrives as refusals under load. The number depends
// on the instance count and the server, which only a deployment knows - so the registration refuses
// to start without one (docs/scale-out-findings.md).
public class ConnectionPoolSizeTests
{
    private const string WithoutAPoolSize = "Host=localhost;Database=staystack;Username=postgres;Password=postgres";
    private const string WithAPoolSize = $"{WithoutAPoolSize};Maximum Pool Size=40";

    [Fact]
    public void AConnectionStringWithoutAPoolSize_RefusesToStart()
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddAppDbContext(WithoutAPoolSize, isDevelopment: false));

        Assert.Contains("Maximum Pool Size", refused.Message);
    }

    [Fact]
    public void AConnectionStringWithAPoolSize_IsAccepted()
    {
        new ServiceCollection().AddAppDbContext(WithAPoolSize, isDevelopment: false);
    }

    [Fact]
    public void Development_NeedsNoPoolSize()
    {
        // One developer, one process: the default is fine, and a required setting here would be
        // friction on every clone for a question only a deployment can answer.
        new ServiceCollection().AddAppDbContext(WithoutAPoolSize, isDevelopment: true);
    }
}
