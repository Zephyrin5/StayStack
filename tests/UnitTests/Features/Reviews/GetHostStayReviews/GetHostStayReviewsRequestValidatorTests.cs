using FluentValidation.TestHelper;
using Reviews.Features.GetHostStayReviews;
namespace UnitTests.Features.Reviews.GetHostStayReviews;

public class GetHostStayReviewsRequestValidatorTests
{
    private readonly GetHostStayReviewsRequestValidator _sut = new GetHostStayReviewsRequestValidator();

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        var result = _sut.TestValidate(new GetHostStayReviewsRequest { Page = 1, PageSize = 20 });

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPage_WhenLessThanOne()
    {
        var result = _sut.TestValidate(new GetHostStayReviewsRequest { Page = 0, PageSize = 20 });

        result.ShouldHaveValidationErrorFor(x => x.Page);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPageSize_WhenZero()
    {
        var result = _sut.TestValidate(new GetHostStayReviewsRequest { Page = 1, PageSize = 0 });

        result.ShouldHaveValidationErrorFor(x => x.PageSize);
    }
}
