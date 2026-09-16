using Reviews.Entities.Configurations;
using Persistence;
using Microsoft.Extensions.Options;
using Bookings.Contracts;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Time;
using Catalog.Contracts;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Reviews.Entities;
using Reviews.Exceptions;
namespace Reviews.Features.CreateGuestReview;

public class CreateGuestReviewHandler(
    ReviewsDb dbContext,
    IBookingLookup bookingLookup,
    IUnitLookup unitLookup,
    IHostAuthorization hostAuthorization,
    TimeProvider timeProvider,
    IOptions<BookingLifecyclePolicyOptions> policy) : IRequestHandler<CreateGuestReviewRequest, CreateGuestReviewResponse>
{
    public async ValueTask<CreateGuestReviewResponse> Handle(CreateGuestReviewRequest request, CancellationToken cancellationToken)
    {
        // Chosen first, before anything that could retry - see docs/adr/0025.
        Guid reviewId = Guid.CreateVersion7();

        Guid hostId = hostAuthorization.RequireHostId();

        // A raw lookup, not an ownership-checked one - a host reviewing a
        // guest is authorized by owning the booking's unit, not by being
        // the customer/guest on it. See IBookingLookup.GetBookingDetailsAsync's
        // own doc comment.
        BookingAccessResult booking = await bookingLookup.GetBookingDetailsAsync(request.BookingId, cancellationToken)
                                       ?? throw new NotFoundException("Booking", request.BookingId);

        UnitSummary unit = await unitLookup.GetUnitIncludingArchivedAsync(booking.UnitId, cancellationToken)
                            ?? throw new NotFoundException("Unit", booking.UnitId);

        // Not found, not forbidden, for a unit belonging to another host -
        // same "doesn't exist and isn't yours must look identical"
        // reasoning as IHostAuthorization.RequireOwnership itself.
        if (unit.HostId != hostId)
        {
            throw new NotFoundException("Booking", request.BookingId);
        }

        if (!booking.IsConfirmed)
        {
            throw new ValidationException(nameof(request.BookingId), "This booking hasn't been confirmed yet.");
        }

        // The booking's own snapshotted zone - "has the stay ended" is a
        // question about the property's calendar, and this must match
        // GetBookingForManagementHandler's CanReview exactly or the UI offers
        // a review the API rejects. See docs/adr/0018.
        DateOnly today = PropertyTimeZone.Today(timeProvider, booking.TimeZoneId);

        // Same window as the guest's own review, deliberately: a one-sided
        // deadline would let a host review a guest who can no longer answer.
        int reviewWindowDays = policy.Value.ReviewWindowDaysAfterCheckOut;
        if (booking.CheckOut <= today && today > booking.CheckOut.AddDays(reviewWindowDays))
        {
            throw new ValidationException(
                nameof(request.BookingId),
                $"Reviews close {reviewWindowDays} days after checkout, and this stay is past that.");
        }

        if (booking.CheckOut > today)
        {
            throw new ValidationException(nameof(request.BookingId), "This stay hasn't ended yet.");
        }

        bool alreadyReviewed = await dbContext.GuestReviews
            .AnyAsync(r => r.BookingId == request.BookingId, cancellationToken);
        if (alreadyReviewed)
        {
            throw new GuestAlreadyReviewedException(request.BookingId);
        }

        GuestReview review = GuestReview.Create(reviewId, request.BookingId, hostId, booking.GuestEmail, request.OverallRating, request.Comment);
        dbContext.GuestReviews.Add(review);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsPrimaryKeyViolationOf(dbContext.GuestReviews))
        {
            // An earlier attempt committed and lost its acknowledgement - answer
            // with that row (Persistence.CommittedInsertRecovery).
            GuestReview committed = await dbContext.GuestReviews.FindOwnCommittedInsertAsync(review.Id, cancellationToken);
            return new CreateGuestReviewResponse { GuestReviewId = committed.Id };
        }
        catch (DbUpdateException ex) when (ex.IsViolationOf(GuestReviewConfiguration.BookingIndex))
        {
            throw new GuestAlreadyReviewedException(request.BookingId);
        }

        return new CreateGuestReviewResponse { GuestReviewId = review.Id };
    }
}
