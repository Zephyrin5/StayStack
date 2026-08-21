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
            UnitType = UnitType.Room,
            Name = new Dictionary<string, string> { { "en", "Deluxe Room" } },
            MaxOccupancy = 2,
            BasePrice = 45.5m,
            Currency = "KWD"
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        CreateUnitRequest request = CreateValidRequest();

        TestValidationResult<CreateUnitRequest> result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPropertyId_WhenEmpty()
    {
        CreateUnitRequest request = CreateValidRequest() with { PropertyId = Guid.Empty };

        TestValidationResult<CreateUnitRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.PropertyId);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForUnitType_WhenNotAValidEnumMember()
    {
        CreateUnitRequest request = CreateValidRequest() with { UnitType = (UnitType)99 };

        TestValidationResult<CreateUnitRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.UnitType);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForName_WhenEmpty()
    {
        CreateUnitRequest request = CreateValidRequest() with { Name = new Dictionary<string, string>() };

        TestValidationResult<CreateUnitRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldHaveError_ForMaxOccupancy_WhenNotPositive(int maxOccupancy)
    {
        CreateUnitRequest request = CreateValidRequest() with { MaxOccupancy = maxOccupancy };

        TestValidationResult<CreateUnitRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.MaxOccupancy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10.5)]
    public void Validate_ShouldHaveError_ForBasePrice_WhenNotPositive(decimal basePrice)
    {
        CreateUnitRequest request = CreateValidRequest() with { BasePrice = basePrice };

        TestValidationResult<CreateUnitRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.BasePrice);
    }

    [Theory]
    [InlineData("K")]
    [InlineData("KWDD")]
    public void Validate_ShouldHaveError_ForCurrency_WhenNotExactlyThreeCharacters(string currency)
    {
        CreateUnitRequest request = CreateValidRequest() with { Currency = currency };

        TestValidationResult<CreateUnitRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Currency);
    }
}
