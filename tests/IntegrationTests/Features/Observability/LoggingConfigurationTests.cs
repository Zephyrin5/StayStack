using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace IntegrationTests.Features.Observability;

// Reads the levels through the running host's own IConfiguration rather than
// parsing a file by path, so this exercises the real appsettings stack. The
// test host is "Testing", and appsettings.Testing.json sets no Logging
// section, so what these assert is the production value inherited from
// appsettings.json.
[Collection("Integration Tests")]
public class LoggingConfigurationTests(IntegrationTestWebApplicationFactory factory)
{
    [Fact]
    public void EntityFrameworkLogging_IsRaisedToWarning_SoProductionDoesNotLogEverySqlStatement()
    {
        // Logging:LogLevel:Default is Information and only Microsoft.AspNetCore
        // was raised, which left Microsoft.EntityFrameworkCore.Database.Command
        // - Information - writing a line per SQL statement in production.
        //
        // The one filter that suppressed that category lived in
        // ConfigureObservabilityServices, which Program.cs has commented out
        // pending Grafana config, so nothing was applying it. Hence a
        // configuration override, which holds whether or not that registration
        // is ever switched back on.
        using IServiceScope scope = factory.Services.CreateScope();
        IConfiguration configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        Assert.Equal("Warning", configuration["Logging:LogLevel:Microsoft.EntityFrameworkCore"]);
    }

    [Fact]
    public void DefaultLogLevel_StaysInformation_SoTheOverrideIsDoingTheWork()
    {
        // Guards against the override being "fixed" later by lowering Default
        // instead. Default staying at Information is what keeps application
        // logs useful; the EF override above is what keeps that affordable.
        // If this ever changes, the assertion above stops proving anything.
        using IServiceScope scope = factory.Services.CreateScope();
        IConfiguration configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        Assert.Equal("Information", configuration["Logging:LogLevel:Default"]);
    }
}
