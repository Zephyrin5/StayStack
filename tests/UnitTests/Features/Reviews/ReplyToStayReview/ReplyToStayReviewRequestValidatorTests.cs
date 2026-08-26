using FluentValidation.TestHelper;
using Reviews.Features.ReplyToStayReview;
namespace UnitTests.Features.Reviews.ReplyToStayReview;

public class ReplyToStayReviewRequestValidatorTests
{
    private readonly ReplyToStayReviewRequestValidator _sut = new ReplyToStayReviewRequestValidator();

    private static ReplyToStayReviewRequest CreateValidRequest() => new ReplyToStayReviewRequest
    {
        StayReviewId = Guid.NewGuid(),
        ReplyText = "Thanks for staying!"
    };

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        var result = _sut.TestValidate(CreateValidRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForStayReviewId_WhenEmpty()
    {
        ReplyToStayReviewRequest request = CreateValidRequest() with { StayReviewId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.StayReviewId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldHaveError_ForReplyText_WhenEmptyOrWhitespace(string replyText)
    {
        ReplyToStayReviewRequest request = CreateValidRequest() with { ReplyText = replyText };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.ReplyText);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForReplyText_WhenTooLong()
    {
        ReplyToStayReviewRequest request = CreateValidRequest() with { ReplyText = new string('a', 2001) };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.ReplyText);
    }
}
