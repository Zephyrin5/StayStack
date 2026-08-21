using Catalog.Entities;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace UnitTests.Entities;

// "UnitEntityTests", not "UnitTests" - the latter would be a confusing name
// for a test class living inside the UnitTests project/assembly.
public class UnitEntityTests
{
    private static LocalizedText CreateName()
    {
        return LocalizedText.Create(new Dictionary<string, string> { { "en", "Deluxe Room" } }, "en");
    }

    [Fact]
    public void Create_ShouldSetAllProperties_WhenInputIsValid()
    {
        Guid propertyId = Guid.NewGuid();
        LocalizedText name = CreateName();

        Unit unit = Unit.Create(propertyId, UnitType.Room, name, 2, 45.5m, "KWD");

        Assert.NotEqual(Guid.Empty, unit.Id);
        Assert.Equal(propertyId, unit.PropertyId);
        Assert.Equal(UnitType.Room, unit.UnitType);
        Assert.Equal(name, unit.Name);
        Assert.Equal(2, unit.MaxOccupancy);
        Assert.Equal(45.5m, unit.BasePrice);
        Assert.Equal("KWD", unit.Currency);
        Assert.Equal(EntityStatus.Active, unit.Status);
    }

    [Fact]
    public void Create_ShouldDefaultCurrencyToKwd_WhenNotSpecified()
    {
        Unit unit = Unit.Create(Guid.NewGuid(), UnitType.Room, CreateName(), 2, 45.5m);

        Assert.Equal("KWD", unit.Currency);
    }

    [Fact]
    public void Create_ShouldThrow_WhenPropertyIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => Unit.Create(Guid.Empty, UnitType.Room, CreateName(), 2, 45.5m));
    }

    [Fact]
    public void Create_ShouldThrow_WhenNameIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => Unit.Create(Guid.NewGuid(), UnitType.Room, null!, 2, 45.5m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_ShouldThrow_WhenMaxOccupancyIsNotPositive(int maxOccupancy)
    {
        Assert.ThrowsAny<ArgumentException>(() => Unit.Create(Guid.NewGuid(), UnitType.Room, CreateName(), maxOccupancy, 45.5m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10.5)]
    public void Create_ShouldThrow_WhenBasePriceIsNotPositive(decimal basePrice)
    {
        Assert.ThrowsAny<ArgumentException>(() => Unit.Create(Guid.NewGuid(), UnitType.Room, CreateName(), 2, basePrice));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldThrow_WhenCurrencyIsNullOrWhitespace(string? currency)
    {
        Assert.ThrowsAny<ArgumentException>(() => Unit.Create(Guid.NewGuid(), UnitType.Room, CreateName(), 2, 45.5m, currency!));
    }

    [Fact]
    public void SetBasePrice_ShouldUpdatePrice_WhenPositive()
    {
        Unit unit = Unit.Create(Guid.NewGuid(), UnitType.Room, CreateName(), 2, 45.5m);

        unit.SetBasePrice(60m);

        Assert.Equal(60m, unit.BasePrice);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SetBasePrice_ShouldThrow_WhenNotPositive(decimal price)
    {
        Unit unit = Unit.Create(Guid.NewGuid(), UnitType.Room, CreateName(), 2, 45.5m);

        Assert.ThrowsAny<ArgumentException>(() => unit.SetBasePrice(price));
    }

    [Fact]
    public void Rename_ShouldUpdateName_WhenValid()
    {
        Unit unit = Unit.Create(Guid.NewGuid(), UnitType.Room, CreateName(), 2, 45.5m);
        LocalizedText newName = LocalizedText.Create(new Dictionary<string, string> { { "en", "Executive Room" } }, "en");

        unit.Rename(newName);

        Assert.Equal(newName, unit.Name);
    }

    [Fact]
    public void Rename_ShouldThrow_WhenNameIsNull()
    {
        Unit unit = Unit.Create(Guid.NewGuid(), UnitType.Room, CreateName(), 2, 45.5m);

        Assert.Throws<ArgumentNullException>(() => unit.Rename(null!));
    }
}
