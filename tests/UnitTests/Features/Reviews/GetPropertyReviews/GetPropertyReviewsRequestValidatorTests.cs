using FluentValidation.TestHelper;
using Reviews.Features.GetPropertyReviews;
namespace UnitTests.Features.Reviews.GetPropertyReviews;

public class GetPropertyReviewsRequestValidatorTests
{
    private readonly GetPropertyReviewsRequestValidator _sut = new GetPropertyReviewsRequestValidator();

    private static GetPropertyReviewsRequest CreateValidRequest() => new GetPropertyReviewsRequest
    {
        PropertyId = Guid.NewGuid(),
        Page = 1,
        PageSize = 20
    };

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        var result = _sut.TestValidate(CreateValidRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPropertyId_WhenEmpty()
    {
        GetPropertyReviewsRequest request = CreateValidRequest() with { PropertyId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.PropertyId);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPage_WhenLessThanOne()
    {
        GetPropertyReviewsRequest request = CreateValidRequest() with { Page = 0 };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Page);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPageSize_WhenZero()
    {
        GetPropertyReviewsRequest request = CreateValidRequest() with { PageSize = 0 };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.PageSize);
    }
}
