using FluentValidation.TestHelper;
using Reviews.Features.CreateGuestReview;
namespace UnitTests.Features.Reviews.CreateGuestReview;

public class CreateGuestReviewRequestValidatorTests
{
    private readonly CreateGuestReviewRequestValidator _sut = new CreateGuestReviewRequestValidator();

    private static CreateGuestReviewRequest CreateValidRequest() => new CreateGuestReviewRequest
    {
        BookingId = Guid.NewGuid(),
        OverallRating = 5,
        Comment = "Great guest"
    };

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        var result = _sut.TestValidate(CreateValidRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForBookingId_WhenEmpty()
    {
        CreateGuestReviewRequest request = CreateValidRequest() with { BookingId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.BookingId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Validate_ShouldHaveError_ForOverallRating_WhenOutOfRange(int rating)
    {
        CreateGuestReviewRequest request = CreateValidRequest() with { OverallRating = rating };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.OverallRating);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForComment_WhenTooLong()
    {
        CreateGuestReviewRequest request = CreateValidRequest() with { Comment = new string('a', 2001) };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Comment);
    }
}
