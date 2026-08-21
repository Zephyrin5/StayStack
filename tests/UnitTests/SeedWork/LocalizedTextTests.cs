using SeedWork.ValueObjects;
namespace UnitTests.SeedWork;

public class LocalizedTextTests
{
    [Fact]
    public void Create_WithoutRequiredLanguage_ThrowsArgumentException()
    {
        var dict = new Dictionary<string, string> { { "fr", "Bonjour" } };

        Assert.Throws<ArgumentException>(() => LocalizedText.Create(dict, "en"));
    }

    [Fact]
    public void GetOrFallback_MissingRequestedAndFallback_ReturnsFirstAvailable()
    {
        LocalizedText text = LocalizedText.Restore(new Dictionary<string, string> { { "de", "Hallo" } });

        string result = text.GetOrFallback("en", "fr");

        Assert.Equal("Hallo", result);
    }

    [Theory]
    [InlineData("en", "en", "fr", "Hello")] // Exact match
    [InlineData("de", "fr", "fr", "Bonjour")] // Requested missing -> Fallback
    [InlineData("de", "es", "fr", "Hello")] // Both missing -> First available
    public void GetOrFallback_ResolvesCorrectLanguageCode(
        string requested,
        string fallback,
        string primaryAvailable,
        string expectedResult)
    {
        var dict = new Dictionary<string, string>
        {
            { "en", "Hello" },
            { "fr", "Bonjour" }
        };
        LocalizedText localizedText = LocalizedText.Restore(dict);

        string actual = localizedText.GetOrFallback(requested, fallback);

        Assert.Equal(expectedResult, actual);
    }

    [Fact]
    public void Equals_SameDictionaryContentsDifferentOrder_ReturnsTrue()
    {
        var dictA = new Dictionary<string, string> { { "en", "Hi" }, { "es", "Hola" } };
        var dictB = new Dictionary<string, string> { { "es", "Hola" }, { "en", "Hi" } };

        LocalizedText textA = LocalizedText.Restore(dictA);
        LocalizedText textB = LocalizedText.Restore(dictB);

        Assert.Equal(textA, textB);
        Assert.Equal(textA.GetHashCode(), textB.GetHashCode());
    }
}
