using Bookings.Contracts;
using BuildingBlocks.Exceptions;
using Catalog.Contracts;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reviews.Entities;
using Reviews.Exceptions;
namespace Reviews.Features.CreateGuestReview;

public class CreateGuestReviewHandler(
    AppReviewsDbContext dbContext,
    IBookingLookup bookingLookup,
    IUnitLookup unitLookup,
    IHostAuthorization hostAuthorization,
    TimeProvider timeProvider) : IRequestHandler<CreateGuestReviewRequest, CreateGuestReviewResponse>
{
    public async ValueTask<CreateGuestReviewResponse> Handle(CreateGuestReviewRequest request, CancellationToken cancellationToken)
    {
        Guid hostId = hostAuthorization.RequireHostId();

        // A raw lookup, not an ownership-checked one - a host reviewing a
        // guest is authorized by owning the booking's unit, not by being
        // the customer/guest on it. See IBookingLookup.GetBookingDetailsAsync's
        // own doc comment.
        BookingAccessResult booking = await bookingLookup.GetBookingDetailsAsync(request.BookingId, cancellationToken)
                                       ?? throw new NotFoundException("Booking", request.BookingId);

        UnitSummary unit = await unitLookup.GetUnitAsync(booking.UnitId, cancellationToken)
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

        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
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

        GuestReview review = GuestReview.Create(request.BookingId, hostId, booking.GuestEmail, request.OverallRating, request.Comment);
        dbContext.GuestReviews.Add(review);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new GuestAlreadyReviewedException(request.BookingId);
        }

        return new CreateGuestReviewResponse { GuestReviewId = review.Id };
    }
}
