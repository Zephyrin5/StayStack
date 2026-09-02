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

        // One instant for the whole request, converted per booking's own zone.
        // The ZONE has to be per booking - these span properties in different
        // ones, so a single "today" would be structurally wrong, and the filter
        // runs before any unit is loaded so the booking's snapshot is all there
        // is (docs/adr/0018). The INSTANT does not: reading the clock per
        // booking meant one list could be evaluated against several "now"s.
        DateTimeOffset now = timeProvider.GetUtcNow();
        int reviewWindowDays = policy.Value.ReviewWindowDaysAfterCheckOut;

        // Both bounds, so this list never offers a review CreateStayReview
        // would then reject - the same "UI must agree with the API" rule the
        // lower bound already had.
        bool IsReviewable(BookingAccessResult b)
        {
            DateOnly today = PropertyTimeZone.ToLocalDate(now, b.TimeZoneId);
            return b.CheckOut <= today && today <= b.CheckOut.AddDays(reviewWindowDays);
        }

        // Reviews has no notion of Booking/CustomerId itself, so the confirmed
        // bookings come cross-module - same reasoning GetHostBookingsHandler
        // uses for UnitId->HostId.
        //
        // Bounded by checkout date rather than fetched wholesale. The result
        // can only ever span the review window, so loading a customer's entire
        // history to filter it in memory was work proportional to how long
        // they have been a customer, on an endpoint with no pagination.
        //
        // Widened a day either side because the range is evaluated in UTC
        // while the real test is property-local. A local date is within one
        // day of the UTC date in every timezone, so this is a safe superset;
        // IsReviewable below applies the exact per-zone bounds to it. Erring
        // wide costs at most two extra days of rows and cannot drop a booking
        // that belongs in the list.
        DateOnly utcToday = DateOnly.FromDateTime(now.UtcDateTime);

        IReadOnlyList<BookingAccessResult> confirmedBookings =
            await bookingLookup.GetConfirmedBookingsForCustomerAsync(
                customerId,
                utcToday.AddDays(-(reviewWindowDays + 1)),
                utcToday.AddDays(1),
                cancellationToken);

        // Materialized once and reused. This used to be evaluated twice over
        // the whole list - once to build the ids to check for existing
        // reviews, then again alongside that check - which did the timezone
        // resolution for every booking twice over an unbounded history.
        //
        // It also removed a real, if remote, hazard: the two passes each read
        // the clock, so they could straddle a local midnight and disagree
        // about the same booking. A booking crossing INTO the window between
        // them would appear in the result without its review status ever
        // having been queried.
        List<BookingAccessResult> candidates = [.. confirmedBookings.Where(IsReviewable)];
        List<Guid> candidateIds = [.. candidates.Select(b => b.BookingId)];

        HashSet<Guid> alreadyReviewedBookingIds = (await dbContext.StayReviews
            .AsNoTracking()
            .Where(r => candidateIds.Contains(r.BookingId))
            .Select(r => r.BookingId)
            .ToListAsync(cancellationToken)).ToHashSet();

        List<BookingAccessResult> reviewable =
            [.. candidates.Where(b => !alreadyReviewedBookingIds.Contains(b.BookingId))];

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
