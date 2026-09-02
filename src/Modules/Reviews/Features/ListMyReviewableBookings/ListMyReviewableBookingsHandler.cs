using Microsoft.Extensions.Options;
using Bookings.Contracts;
using BuildingBlocks.Time;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Reviews.Features.ListMyReviewableBookings;

public class ListMyReviewableBookingsHandler(
    AppReviewsDbContext dbContext,
    IBookingLookup bookingLookup,
    IUnitLookup unitLookup,
    ICurrentUserProvider currentUserProvider,
    TimeProvider timeProvider,
    IOptions<BookingLifecyclePolicyOptions> policy) : IRequestHandler<ListMyReviewableBookingsRequest, ListMyReviewableBookingsResponse>
{
    public async ValueTask<ListMyReviewableBookingsResponse> Handle(
        ListMyReviewableBookingsRequest request, CancellationToken cancellationToken)
    {
        // Not null-checked - this endpoint requires authentication (no
        // AllowAnonymous), so the framework already guarantees a caller
        // reaching this handler has a UserId, same trust
        // GetMyBookingsHandler already places in this exact value.
        Guid customerId = currentUserProvider.UserId!.Value;

        // Reviews has no notion of Booking/CustomerId itself - every
        // Confirmed booking has to be resolved cross-module first, same
        // reasoning GetHostBookingsHandler already uses for UnitId->HostId.
        IReadOnlyList<BookingAccessResult> confirmedBookings =
            await bookingLookup.GetConfirmedBookingsForCustomerAsync(customerId, cancellationToken);

        // Per booking, not once for the whole list. These bookings can span
        // properties in different zones, so a single "today" is structurally
        // wrong here regardless of which zone it is computed in - and the
        // filter runs before any unit is loaded, so the booking's own
        // snapshot is the only thing available. See docs/adr/0018.
        // Both bounds, so this list never offers a review CreateStayReview
        // would then reject - the same "UI must agree with the API" rule the
        // lower bound already had.
        bool IsReviewable(BookingAccessResult b)
        {
            DateOnly today = PropertyTimeZone.Today(timeProvider, b.TimeZoneId);
            return b.CheckOut <= today
                   && today <= b.CheckOut.AddDays(policy.Value.ReviewWindowDaysAfterCheckOut);
        }

        List<Guid> pastBookingIds = confirmedBookings
            .Where(IsReviewable)
            .Select(b => b.BookingId)
            .ToList();

        HashSet<Guid> alreadyReviewedBookingIds = (await dbContext.StayReviews
            .AsNoTracking()
            .Where(r => pastBookingIds.Contains(r.BookingId))
            .Select(r => r.BookingId)
            .ToListAsync(cancellationToken)).ToHashSet();

        List<BookingAccessResult> reviewable = confirmedBookings
            .Where(b => IsReviewable(b) && !alreadyReviewedBookingIds.Contains(b.BookingId))
            .ToList();

        // One batched lookup for every distinct unit, not one call per
        // booking - same reasoning as GetMyBookingsHandler.
        IReadOnlyDictionary<Guid, UnitSummary> unitsById = await unitLookup.GetUnitsAsync(
            reviewable.Select(b => b.UnitId).Distinct(), cancellationToken);

        return new ListMyReviewableBookingsResponse
        {
            Bookings =
            [
                .. reviewable.Select(b => new ReviewableBookingSummary
                {
                    BookingId = b.BookingId,
                    UnitId = b.UnitId,
                    UnitName = unitsById.TryGetValue(b.UnitId, out UnitSummary? unit) ? unit.Name : new Dictionary<string, string>(),
                    CheckIn = b.CheckIn,
                    CheckOut = b.CheckOut
                })
            ]
        };
    }
}
