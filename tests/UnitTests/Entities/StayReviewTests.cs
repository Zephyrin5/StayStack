using Reviews.Entities;
using Reviews.Exceptions;
namespace UnitTests.Entities;

public class StayReviewTests
{
    private static StayReview CreateValidReview(
        int cleanliness = 5, int communication = 4, int location = 3, int value = 2, int accuracy = 1) =>
        StayReview.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest@Example.com ",
            cleanliness, communication, location, value, accuracy, "Great stay");

    [Fact]
    public void Create_ShouldSetAllProperties_WhenInputIsValid()
    {
        Guid bookingId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid hostId = Guid.NewGuid();
        Guid reviewerCustomerId = Guid.NewGuid();

        StayReview review = StayReview.Create(
            bookingId, propertyId, hostId, reviewerCustomerId, "Guest@Example.com ",
            5, 4, 3, 2, 1, "Great stay");

        Assert.NotEqual(Guid.Empty, review.Id);
        Assert.Equal(bookingId, review.BookingId);
        Assert.Equal(propertyId, review.PropertyId);
        Assert.Equal(hostId, review.HostId);
        Assert.Equal(reviewerCustomerId, review.ReviewerCustomerId);
        Assert.Equal("guest@example.com", review.ReviewerGuestEmail);
        Assert.Equal(5, review.CleanlinessRating);
        Assert.Equal(4, review.CommunicationRating);
        Assert.Equal(3, review.LocationRating);
        Assert.Equal(2, review.ValueRating);
        Assert.Equal(1, review.AccuracyRating);
        Assert.Equal("Great stay", review.Comment);
        Assert.Null(review.HostReplyText);
        Assert.Null(review.HostRepliedAt);
    }

    [Fact]
    public void Create_ShouldComputeOverallRatingAsTheAverageOfTheFiveCategories()
    {
        StayReview review = StayReview.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "guest@example.com",
            5, 4, 3, 2, 1, null);

        Assert.Equal(3m, review.OverallRating);
    }

    [Fact]
    public void Create_ShouldAllowNullReviewerCustomerId_ForGuestCheckout()
    {
        StayReview review = StayReview.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "guest@example.com",
            5, 5, 5, 5, 5, null);

        Assert.Null(review.ReviewerCustomerId);
    }

    [Fact]
    public void Create_ShouldThrow_WhenBookingIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => StayReview.Create(
            Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), null, "guest@example.com", 5, 5, 5, 5, 5, null));
    }

    [Fact]
    public void Create_ShouldThrow_WhenPropertyIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => StayReview.Create(
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), null, "guest@example.com", 5, 5, 5, 5, 5, null));
    }

    [Fact]
    public void Create_ShouldThrow_WhenHostIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => StayReview.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, null, "guest@example.com", 5, 5, 5, 5, 5, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldThrow_WhenReviewerGuestEmailIsNullOrWhitespace(string? guestEmail)
    {
        Assert.ThrowsAny<ArgumentException>(() => StayReview.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, guestEmail!, 5, 5, 5, 5, 5, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Create_ShouldThrow_WhenAnyCategoryRatingIsOutOfRange(int invalidRating)
    {
        Assert.ThrowsAny<ArgumentException>(() => StayReview.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "guest@example.com",
            invalidRating, 5, 5, 5, 5, null));
    }

    [Fact]
    public void Reply_ShouldSetHostReplyTextAndHostRepliedAt()
    {
        StayReview review = CreateValidReview();
        DateTimeOffset repliedAt = DateTimeOffset.UtcNow;

        review.Reply("Thanks for staying!", repliedAt);

        Assert.Equal("Thanks for staying!", review.HostReplyText);
        Assert.Equal(repliedAt, review.HostRepliedAt);
    }

    [Fact]
    public void Reply_ShouldThrow_WhenAlreadyReplied()
    {
        StayReview review = CreateValidReview();
        review.Reply("First reply", DateTimeOffset.UtcNow);

        Assert.Throws<ReviewAlreadyRepliedException>(() => review.Reply("Second reply", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reply_ShouldThrow_WhenReplyTextIsNullOrWhitespace(string? replyText)
    {
        StayReview review = CreateValidReview();

        Assert.ThrowsAny<ArgumentException>(() => review.Reply(replyText!, DateTimeOffset.UtcNow));
    }
}
