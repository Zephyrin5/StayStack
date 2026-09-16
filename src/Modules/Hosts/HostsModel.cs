using Hosts.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Hosts;

public sealed class HostsModel : IModuleModel
{
    /// <summary>This module's Postgres schema. Its tables and its raw SQL name it (docs/adr/0004).</summary>
    public const string Schema = "hosts";

    public void Configure(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new HostConfiguration());
    }
}
