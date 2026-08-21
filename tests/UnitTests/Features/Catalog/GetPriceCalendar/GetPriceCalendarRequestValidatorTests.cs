using Catalog.Features.GetPriceCalendar;
using FluentValidation.TestHelper;
namespace UnitTests.Features.Catalog.GetPriceCalendar;

public class GetPriceCalendarRequestValidatorTests
{
    private readonly GetPriceCalendarRequestValidator _sut = new GetPriceCalendarRequestValidator();

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static GetPriceCalendarRequest CreateValidRequest()
    {
        return new GetPriceCalendarRequest
        {
            UnitId = Guid.NewGuid(),
            From = Today,
            To = Today.AddDays(7)
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        GetPriceCalendarRequest request = CreateValidRequest();

        TestValidationResult<GetPriceCalendarRequest> result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForUnitId_WhenEmpty()
    {
        GetPriceCalendarRequest request = CreateValidRequest() with { UnitId = Guid.Empty };

        TestValidationResult<GetPriceCalendarRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.UnitId);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForTo_WhenNotAfterFrom()
    {
        GetPriceCalendarRequest request = CreateValidRequest() with { To = Today };

        TestValidationResult<GetPriceCalendarRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.To);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForTo_WhenBeforeFrom()
    {
        GetPriceCalendarRequest request = CreateValidRequest() with { To = Today.AddDays(-1) };

        TestValidationResult<GetPriceCalendarRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.To);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForRange_WhenSpanExceeds366Days()
    {
        GetPriceCalendarRequest request = CreateValidRequest() with { From = Today, To = Today.AddDays(367) };

        TestValidationResult<GetPriceCalendarRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor("range");
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForRange_WhenSpanIsExactly366Days()
    {
        GetPriceCalendarRequest request = CreateValidRequest() with { From = Today, To = Today.AddDays(366) };

        TestValidationResult<GetPriceCalendarRequest> result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor("range");
    }
}
