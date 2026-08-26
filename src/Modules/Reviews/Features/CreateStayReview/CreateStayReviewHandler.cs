using Bookings.Contracts;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reviews.Entities;
namespace Reviews.Features.CreateStayReview;

public class CreateStayReviewHandler(
    AppReviewsDbContext dbContext,
    IBookingLookup bookingLookup,
    IUnitLookup unitLookup,
    ICurrentUserProvider currentUserProvider,
    TimeProvider timeProvider) : IRequestHandler<CreateStayReviewRequest, CreateStayReviewResponse>
{
    public async ValueTask<CreateStayReviewResponse> Handle(CreateStayReviewRequest request, CancellationToken cancellationToken)
    {
        // Same two-path ownership proof CancelBookingHandler itself uses -
        // an authenticated customer's own id, or a guest-checkout
        // management token - resolved cross-module without Reviews ever
        // seeing Booking or AppBookingsDbContext.
        BookingAccessResult access = await bookingLookup.VerifyBookingAccessAsync(
                                          request.BookingId, currentUserProvider.UserId, request.ManagementToken, cancellationToken)
                                      ?? throw new NotFoundException("Booking", request.BookingId);

        if (!access.IsConfirmed)
        {
            throw new ValidationException(nameof(request.BookingId), "This booking hasn't been confirmed yet.");
        }

        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (access.CheckOut > today)
        {
            throw new ValidationException(nameof(request.BookingId), "This stay hasn't ended yet.");
        }

        // Friendly-error fast path - not what actually prevents a second
        // review under a race, the unique index on StayReview.BookingId is
        // (see the catch below), same "check-first is an optimization, the
        // constraint is the real guarantee" idiom as InitiateTransactionHandler.
        bool alreadyReviewed = await dbContext.StayReviews
            .AnyAsync(r => r.BookingId == request.BookingId, cancellationToken);
        if (alreadyReviewed)
        {
            throw new ValidationException(nameof(request.BookingId), "This stay has already been reviewed.");
        }

        UnitSummary unit = await unitLookup.GetUnitAsync(access.UnitId, cancellationToken)
                            ?? throw new NotFoundException("Unit", access.UnitId);

        StayReview review = StayReview.Create(
            request.BookingId,
            unit.PropertyId,
            unit.HostId,
            access.CustomerId,
            access.GuestEmail,
            request.CleanlinessRating,
            request.CommunicationRating,
            request.LocationRating,
            request.ValueRating,
            request.AccuracyRating,
            request.Comment);

        dbContext.StayReviews.Add(review);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ValidationException(nameof(request.BookingId), "This stay has already been reviewed.");
        }

        return new CreateStayReviewResponse { StayReviewId = review.Id };
    }
}
