using Catalog.Enums;
using Catalog.Features.AdminCreateProperty;
using FluentValidation.TestHelper;
namespace UnitTests.Features.Catalog.AdminCreateProperty;

public class AdminCreatePropertyRequestValidatorTests
{
    private readonly AdminCreatePropertyRequestValidator _sut = new AdminCreatePropertyRequestValidator();

    private static AdminCreatePropertyRequest CreateValidRequest()
    {
        return new AdminCreatePropertyRequest
        {
            HostId = Guid.NewGuid(),
            PropertyType = PropertyType.Chalet,
            Name = new Dictionary<string, string> { { "en", "Desert Chalet" } },
            City = "Al Ahmadi"
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        AdminCreatePropertyRequest request = CreateValidRequest();

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForHostId_WhenEmpty()
    {
        AdminCreatePropertyRequest request = CreateValidRequest() with { HostId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.HostId);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPropertyType_WhenNotAValidEnumMember()
    {
        AdminCreatePropertyRequest request = CreateValidRequest() with { PropertyType = (PropertyType)99 };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.PropertyType);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForName_WhenEmpty()
    {
        AdminCreatePropertyRequest request = CreateValidRequest() with { Name = new Dictionary<string, string>() };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCity_WhenTooLong()
    {
        AdminCreatePropertyRequest request = CreateValidRequest() with { City = new string('a', 101) };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.City);
    }
}
