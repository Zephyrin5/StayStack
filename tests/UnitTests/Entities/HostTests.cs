using Hosts.Entities;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace UnitTests.Entities;

public class HostTests
{
    [Fact]
    public void Create_ShouldSetAllProperties_WhenInputIsValid()
    {
        Host host = Host.Create("Gulf Stays Co.", "contact@gulfstays.example", "+965 1234 5678");

        Assert.NotEqual(Guid.Empty, host.Id);
        Assert.Equal("Gulf Stays Co.", host.BusinessName);
        Assert.Equal("contact@gulfstays.example", host.ContactEmail);
        Assert.Equal("+965 1234 5678", host.ContactPhone);
        Assert.Null(host.DisplayName);
        Assert.Equal(EntityStatus.Active, host.Status);
    }

    [Fact]
    public void Create_ShouldAllowNullContactPhone_AndNullDisplayName()
    {
        Host host = Host.Create("Gulf Stays Co.", "contact@gulfstays.example", null);

        Assert.Null(host.ContactPhone);
        Assert.Null(host.DisplayName);
    }

    [Fact]
    public void Create_ShouldSetDisplayName_WhenProvided()
    {
        LocalizedText displayName = LocalizedText.Create(new Dictionary<string, string> { { "en", "Gulf Stays" } }, "en");

        Host host = Host.Create("Gulf Stays Co.", "contact@gulfstays.example", null, displayName);

        Assert.Equal(displayName, host.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldThrow_WhenBusinessNameIsNullOrWhitespace(string? businessName)
    {
        Assert.ThrowsAny<ArgumentException>(() => Host.Create(businessName!, "contact@gulfstays.example", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing-domain@")]
    [InlineData("@missing-local-part.com")]
    public void Create_ShouldThrow_WhenContactEmailIsInvalid(string? contactEmail)
    {
        Assert.ThrowsAny<ArgumentException>(() => Host.Create("Gulf Stays Co.", contactEmail!, null));
    }

    [Fact]
    public void UpdateContactInfo_ShouldUpdateEmailAndPhone_WhenValid()
    {
        Host host = Host.Create("Gulf Stays Co.", "old@gulfstays.example", "+965 1111 1111");

        host.UpdateContactInfo("new@gulfstays.example", "+965 2222 2222");

        Assert.Equal("new@gulfstays.example", host.ContactEmail);
        Assert.Equal("+965 2222 2222", host.ContactPhone);
    }

    [Fact]
    public void UpdateContactInfo_ShouldThrow_WhenEmailIsInvalid()
    {
        Host host = Host.Create("Gulf Stays Co.", "old@gulfstays.example", null);

        Assert.ThrowsAny<ArgumentException>(() => host.UpdateContactInfo("not-an-email", null));
    }

    [Fact]
    public void SetDisplayName_ShouldAllowClearingBackToNull()
    {
        LocalizedText displayName = LocalizedText.Create(new Dictionary<string, string> { { "en", "Gulf Stays" } }, "en");
        Host host = Host.Create("Gulf Stays Co.", "contact@gulfstays.example", null, displayName);

        host.SetDisplayName(null);

        Assert.Null(host.DisplayName);
    }

    [Fact]
    public void Archive_ShouldSetStatusToArchived_AndRecordWhoAndWhen()
    {
        Host host = Host.Create("Gulf Stays Co.", "contact@gulfstays.example", null);
        DateTimeOffset archivedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Guid archivedBy = Guid.NewGuid();

        host.Archive(archivedAt, archivedBy);

        Assert.Equal(EntityStatus.Archived, host.Status);
        Assert.Equal(archivedAt, host.ModifiedAt);
        Assert.Equal(archivedBy, host.ModifiedBy);
    }
}
