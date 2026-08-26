using Reviews.Entities;
namespace UnitTests.Entities;

public class GuestReviewTests
{
    [Fact]
    public void Create_ShouldSetAllProperties_WhenInputIsValid()
    {
        Guid bookingId = Guid.NewGuid();
        Guid hostId = Guid.NewGuid();

        GuestReview review = GuestReview.Create(bookingId, hostId, "Guest@Example.com ", 4, "Great guest");

        Assert.NotEqual(Guid.Empty, review.Id);
        Assert.Equal(bookingId, review.BookingId);
        Assert.Equal(hostId, review.HostId);
        Assert.Equal("guest@example.com", review.GuestEmail);
        Assert.Equal(4, review.OverallRating);
        Assert.Equal("Great guest", review.Comment);
    }

    [Fact]
    public void Create_ShouldAllowNullComment()
    {
        GuestReview review = GuestReview.Create(Guid.NewGuid(), Guid.NewGuid(), "guest@example.com", 5, null);

        Assert.Null(review.Comment);
    }

    [Fact]
    public void Create_ShouldThrow_WhenBookingIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => GuestReview.Create(
            Guid.Empty, Guid.NewGuid(), "guest@example.com", 5, null));
    }

    [Fact]
    public void Create_ShouldThrow_WhenHostIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => GuestReview.Create(
            Guid.NewGuid(), Guid.Empty, "guest@example.com", 5, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldThrow_WhenGuestEmailIsNullOrWhitespace(string? guestEmail)
    {
        Assert.ThrowsAny<ArgumentException>(() => GuestReview.Create(
            Guid.NewGuid(), Guid.NewGuid(), guestEmail!, 5, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Create_ShouldThrow_WhenOverallRatingIsOutOfRange(int invalidRating)
    {
        Assert.ThrowsAny<ArgumentException>(() => GuestReview.Create(
            Guid.NewGuid(), Guid.NewGuid(), "guest@example.com", invalidRating, null));
    }
}
