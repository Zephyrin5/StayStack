using FastEndpoints;
namespace Api.Endpoints.Availability;

public sealed class AvailabilityGroup : Group
{
    public AvailabilityGroup()
    {
        Configure("api/availability", ep => { ep.Description(b => b.WithTags("Availability")); });
    }
}
