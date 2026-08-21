using Catalog.Features.CreateProperty;
using FluentValidation.TestHelper;
using SeedWork.Enums;
namespace UnitTests.Features.Catalog.CreateProperty;

public class CreatePropertyRequestValidatorTests
{
    private readonly CreatePropertyRequestValidator _sut = new CreatePropertyRequestValidator();

    private static CreatePropertyRequest CreateValidRequest()
    {
        return new CreatePropertyRequest
        {
            PropertyType = PropertyType.Hotel,
            Name = new Dictionary<string, string> { { "en", "Seaside Hotel" } },
            City = "Kuwait City"
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        CreatePropertyRequest request = CreateValidRequest();

        TestValidationResult<CreatePropertyRequest> result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForCity_WhenNull()
    {
        CreatePropertyRequest request = CreateValidRequest() with { City = null };

        TestValidationResult<CreatePropertyRequest> result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.City);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPropertyType_WhenNotAValidEnumMember()
    {
        CreatePropertyRequest request = CreateValidRequest() with { PropertyType = (PropertyType)99 };

        TestValidationResult<CreatePropertyRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.PropertyType);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForName_WhenEmpty()
    {
        CreatePropertyRequest request = CreateValidRequest() with { Name = new Dictionary<string, string>() };

        TestValidationResult<CreatePropertyRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCity_WhenTooLong()
    {
        CreatePropertyRequest request = CreateValidRequest() with { City = new string('a', 101) };

        TestValidationResult<CreatePropertyRequest> result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.City);
    }
}
