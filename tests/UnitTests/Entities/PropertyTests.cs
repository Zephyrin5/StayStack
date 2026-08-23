using Catalog.Entities;
using Catalog.Enums;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace UnitTests.Entities;

public class PropertyTests
{
    private static LocalizedText CreateName()
    {
        return LocalizedText.Create(new Dictionary<string, string> { { "en", "Seaside Hotel" } }, "en");
    }

    [Fact]
    public void Create_ShouldSetAllProperties_WhenInputIsValid()
    {
        Guid hostId = Guid.NewGuid();
        LocalizedText name = CreateName();

        Property property = Property.Create(hostId, PropertyType.Hotel, name, "Kuwait City");

        Assert.NotEqual(Guid.Empty, property.Id);
        Assert.Equal(hostId, property.HostId);
        Assert.Equal(PropertyType.Hotel, property.PropertyType);
        Assert.Equal(name, property.Name);
        Assert.Equal("Kuwait City", property.City);
        Assert.Equal(EntityStatus.Active, property.Status);
    }

    [Fact]
    public void Create_ShouldAllowNullCity()
    {
        Property property = Property.Create(Guid.NewGuid(), PropertyType.Chalet, CreateName(), null);

        Assert.Null(property.City);
    }

    [Fact]
    public void Create_ShouldThrow_WhenHostIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => Property.Create(Guid.Empty, PropertyType.Hotel, CreateName(), null));
    }

    [Fact]
    public void Create_ShouldThrow_WhenNameIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => Property.Create(Guid.NewGuid(), PropertyType.Hotel, null!, null));
    }

    [Fact]
    public void Rename_ShouldUpdateName_WhenValid()
    {
        Property property = Property.Create(Guid.NewGuid(), PropertyType.Hotel, CreateName(), null);
        LocalizedText newName = LocalizedText.Create(new Dictionary<string, string> { { "en", "Marina Hotel" } }, "en");

        property.Rename(newName);

        Assert.Equal(newName, property.Name);
    }

    [Fact]
    public void Rename_ShouldThrow_WhenNameIsNull()
    {
        Property property = Property.Create(Guid.NewGuid(), PropertyType.Hotel, CreateName(), null);

        Assert.Throws<ArgumentNullException>(() => property.Rename(null!));
    }
}
