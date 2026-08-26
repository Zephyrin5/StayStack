using Ardalis.GuardClauses;
using SeedWork.Abstractions;
using SeedWork.Interfaces;
namespace Reviews.Entities;

// A host's review of a guest - private, host-facing only, the other half
// of the mutual review pair (see StayReview). Single overall rating, not
// multi-category like StayReview - unlike the guest-facing review, nothing
// downstream (search, listing sort) reads this one, so the extra
// granularity buys less.
public sealed class GuestReview : Entity, IAggregateRoot
{
    private GuestReview(Guid id, Guid bookingId, Guid hostId, string guestEmail, int overallRating, string? comment)
    {
        Id = id;
        BookingId = bookingId;
        HostId = hostId;
        GuestEmail = guestEmail;
        OverallRating = overallRating;
        Comment = comment;
    }

    public Guid BookingId { get; private set; }
    public Guid HostId { get; private set; }

    // Normalized - the reviewee's durable identity, works identically for
    // authenticated and guest-checkout customers (both always have
    // Booking.GuestEmail), same role it plays on StayReview and
    // PromotionRedemption.
    public string GuestEmail { get; private set; }

    public int OverallRating { get; private set; }
    public string? Comment { get; private set; }

    public static GuestReview Create(Guid bookingId, Guid hostId, string guestEmail, int overallRating, string? comment)
    {
        Guard.Against.Default(bookingId);
        Guard.Against.Default(hostId);
        Guard.Against.NullOrWhiteSpace(guestEmail);
        Guard.Against.OutOfRange(overallRating, nameof(overallRating), 1, 5);

        return new GuestReview(Guid.CreateVersion7(), bookingId, hostId, guestEmail.Trim().ToLowerInvariant(), overallRating, comment);
    }
}
