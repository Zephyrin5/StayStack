namespace Reviews.Features.ListMyReviewableBookings;

public record ListMyReviewableBookingsResponse
{
    public List<ReviewableBookingSummary> Bookings { get; init; } = [];
}

public record ReviewableBookingSummary
{
    public Guid BookingId { get; init; }
    public Guid UnitId { get; init; }
    public Dictionary<string, string> UnitName { get; init; } = new Dictionary<string, string>();
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
}
