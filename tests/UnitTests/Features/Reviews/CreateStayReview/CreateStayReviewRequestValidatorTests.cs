using FluentValidation.TestHelper;
using Reviews.Features.CreateStayReview;
namespace UnitTests.Features.Reviews.CreateStayReview;

public class CreateStayReviewRequestValidatorTests
{
    private readonly CreateStayReviewRequestValidator _sut = new CreateStayReviewRequestValidator();

    private static CreateStayReviewRequest CreateValidRequest() => new CreateStayReviewRequest
    {
        BookingId = Guid.NewGuid(),
        CleanlinessRating = 5,
        CommunicationRating = 4,
        LocationRating = 3,
        ValueRating = 2,
        AccuracyRating = 1,
        Comment = "Great stay"
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
        CreateStayReviewRequest request = CreateValidRequest() with { BookingId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.BookingId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Validate_ShouldHaveError_ForCleanlinessRating_WhenOutOfRange(int rating)
    {
        CreateStayReviewRequest request = CreateValidRequest() with { CleanlinessRating = rating };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.CleanlinessRating);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Validate_ShouldHaveError_ForCommunicationRating_WhenOutOfRange(int rating)
    {
        CreateStayReviewRequest request = CreateValidRequest() with { CommunicationRating = rating };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.CommunicationRating);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Validate_ShouldHaveError_ForLocationRating_WhenOutOfRange(int rating)
    {
        CreateStayReviewRequest request = CreateValidRequest() with { LocationRating = rating };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.LocationRating);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Validate_ShouldHaveError_ForValueRating_WhenOutOfRange(int rating)
    {
        CreateStayReviewRequest request = CreateValidRequest() with { ValueRating = rating };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.ValueRating);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Validate_ShouldHaveError_ForAccuracyRating_WhenOutOfRange(int rating)
    {
        CreateStayReviewRequest request = CreateValidRequest() with { AccuracyRating = rating };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.AccuracyRating);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForComment_WhenTooLong()
    {
        CreateStayReviewRequest request = CreateValidRequest() with { Comment = new string('a', 2001) };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Comment);
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForComment_WhenNull()
    {
        CreateStayReviewRequest request = CreateValidRequest() with { Comment = null };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.Comment);
    }
}
