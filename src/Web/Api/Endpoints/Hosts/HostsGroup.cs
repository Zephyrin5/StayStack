using FastEndpoints;
namespace Api.Endpoints.Hosts;

public sealed class HostsGroup : Group
{
    public HostsGroup()
    {
        Configure("api/hosts", ep => { ep.Description(b => b.WithTags("Hosts")); });
    }
}
