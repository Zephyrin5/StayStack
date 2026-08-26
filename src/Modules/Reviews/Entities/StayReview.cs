using Ardalis.GuardClauses;
using Reviews.Exceptions;
using SeedWork.Abstractions;
using SeedWork.Interfaces;
namespace Reviews.Entities;

// A guest's review of a stay/property - the public-facing half of the
// mutual review pair (see GuestReview for the private, host-facing half).
// Extends Entity for the soft-delete/admin-archive path - the one
// moderation lever this app has for a review, matching PricingRule/
// Promotion's own "no pre-publish approval, admin can remove after"
// pattern.
public sealed class StayReview : Entity, IAggregateRoot
{
    private StayReview(
        Guid id,
        Guid bookingId,
        Guid propertyId,
        Guid hostId,
        Guid? reviewerCustomerId,
        string reviewerGuestEmail,
        int cleanlinessRating,
        int communicationRating,
        int locationRating,
        int valueRating,
        int accuracyRating,
        string? comment)
    {
        Id = id;
        BookingId = bookingId;
        PropertyId = propertyId;
        HostId = hostId;
        ReviewerCustomerId = reviewerCustomerId;
        ReviewerGuestEmail = reviewerGuestEmail;
        CleanlinessRating = cleanlinessRating;
        CommunicationRating = communicationRating;
        LocationRating = locationRating;
        ValueRating = valueRating;
        AccuracyRating = accuracyRating;
        OverallRating = ComputeOverallRating(cleanlinessRating, communicationRating, locationRating, valueRating, accuracyRating);
        Comment = comment;
    }

    public Guid BookingId { get; private set; }

    // Denormalized from Catalog.Contracts.IUnitLookup at creation time, not
    // re-resolved on every read - same "the price they saw is the price
    // they get" snapshot reasoning already used for a hold's own
    // TotalPrice/Currency. Avoids a join back through Bookings->Catalog
    // every time a property's reviews are listed.
    public Guid PropertyId { get; private set; }
    public Guid HostId { get; private set; }

    // Null for a guest-checkout reviewer. ReviewerGuestEmail is always
    // populated regardless - Booking.GuestEmail exists for every booking,
    // the same durable identity role it already plays for
    // PromotionRedemption.
    public Guid? ReviewerCustomerId { get; private set; }
    public string ReviewerGuestEmail { get; private set; }

    public int CleanlinessRating { get; private set; }
    public int CommunicationRating { get; private set; }
    public int LocationRating { get; private set; }
    public int ValueRating { get; private set; }
    public int AccuracyRating { get; private set; }

    // The average of the five categories - computed once here (a pure
    // function of this row's own inputs, no I/O), stored rather than
    // recomputed on every aggregate read.
    public decimal OverallRating { get; private set; }

    public string? Comment { get; private set; }

    // The one-reply-per-review host response - a field pair, not a
    // separate table, since the agreed scope is one reply, not a thread.
    public string? HostReplyText { get; private set; }
    public DateTimeOffset? HostRepliedAt { get; private set; }

    public static StayReview Create(
        Guid bookingId,
        Guid propertyId,
        Guid hostId,
        Guid? reviewerCustomerId,
        string reviewerGuestEmail,
        int cleanlinessRating,
        int communicationRating,
        int locationRating,
        int valueRating,
        int accuracyRating,
        string? comment)
    {
        Guard.Against.Default(bookingId);
        Guard.Against.Default(propertyId);
        Guard.Against.Default(hostId);
        Guard.Against.NullOrWhiteSpace(reviewerGuestEmail);
        Guard.Against.OutOfRange(cleanlinessRating, nameof(cleanlinessRating), 1, 5);
        Guard.Against.OutOfRange(communicationRating, nameof(communicationRating), 1, 5);
        Guard.Against.OutOfRange(locationRating, nameof(locationRating), 1, 5);
        Guard.Against.OutOfRange(valueRating, nameof(valueRating), 1, 5);
        Guard.Against.OutOfRange(accuracyRating, nameof(accuracyRating), 1, 5);

        return new StayReview(
            Guid.CreateVersion7(), bookingId, propertyId, hostId, reviewerCustomerId,
            reviewerGuestEmail.Trim().ToLowerInvariant(), cleanlinessRating, communicationRating,
            locationRating, valueRating, accuracyRating, comment);
    }

    public void Reply(string replyText, DateTimeOffset repliedAt)
    {
        Guard.Against.NullOrWhiteSpace(replyText);
        if (HostReplyText is not null)
        {
            throw new ReviewAlreadyRepliedException(Id);
        }

        HostReplyText = replyText;
        HostRepliedAt = repliedAt;
    }

    private static decimal ComputeOverallRating(
        int cleanliness, int communication, int location, int value, int accuracy) =>
        (cleanliness + communication + location + value + accuracy) / 5m;
}
