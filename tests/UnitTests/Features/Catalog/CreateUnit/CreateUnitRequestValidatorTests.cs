using Catalog.Features.CreateUnit;
using FluentValidation.TestHelper;
using SeedWork.Enums;
namespace UnitTests.Features.Catalog.CreateUnit;

public class CreateUnitRequestValidatorTests
{
    private readonly CreateUnitRequestValidator _sut = new CreateUnitRequestValidator();

    private static CreateUnitRequest CreateValidRequest()
    {
        return new CreateUnitRequest
        {
            PropertyId = Guid.NewGuid(),
            Name = new Dictionary<string, string> { { "en", "Deluxe Room" } },
            MaxOccupancy = 2,
            BasePrice = 45.5m,
            Currency = Currency.KWD
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        CreateUnitRequest request = CreateValidRequest();

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPropertyId_WhenEmpty()
    {
        CreateUnitRequest request = CreateValidRequest() with { PropertyId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.PropertyId);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForName_WhenEmpty()
    {
        CreateUnitRequest request = CreateValidRequest() with { Name = new Dictionary<string, string>() };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldHaveError_ForMaxOccupancy_WhenNotPositive(int maxOccupancy)
    {
        CreateUnitRequest request = CreateValidRequest() with { MaxOccupancy = maxOccupancy };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.MaxOccupancy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10.5)]
    public void Validate_ShouldHaveError_ForBasePrice_WhenNotPositive(decimal basePrice)
    {
        CreateUnitRequest request = CreateValidRequest() with { BasePrice = basePrice };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.BasePrice);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCurrency_WhenNotAValidEnumMember()
    {
        CreateUnitRequest request = CreateValidRequest() with { Currency = (Currency)99 };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Currency);
    }
}
