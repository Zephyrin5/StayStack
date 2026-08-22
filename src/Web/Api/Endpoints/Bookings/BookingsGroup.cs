using FastEndpoints;
namespace Api.Endpoints.Bookings;

public sealed class BookingsGroup : Group
{
    public BookingsGroup()
    {
        Configure("api/bookings", ep => { ep.Description(b => b.WithTags("Bookings")); });
    }
}
