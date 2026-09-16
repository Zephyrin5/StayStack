using Hosts.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Hosts;

public sealed class HostsModel : IModuleModel
{
    public void Configure(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new HostConfiguration());
    }
}
