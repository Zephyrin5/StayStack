using BuildingBlocks.Security;
using System.Net;
namespace UnitTests.Security;

public class SecureTokenTests
{
    [Fact]
    public void Generate_ShouldProduceDistinctTokens_AcrossManyCalls()
    {
        HashSet<string> tokens = Enumerable.Range(0, 1000).Select(_ => SecureToken.Generate()).ToHashSet();

        Assert.Equal(1000, tokens.Count);
    }

    [Fact]
    public void Generate_ShouldProduceUrlSafeTokens()
    {
        // A management token is handed out as a link query parameter, so the
        // three standard-Base64 characters that need percent-escaping must
        // not appear. Checked over many tokens because '+' and '/' turn up in
        // roughly half of random Base64 strings - a single sample would pass
        // by luck often enough to be worthless.
        foreach (string token in Enumerable.Range(0, 1000).Select(_ => SecureToken.Generate()))
        {
            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
        }
    }

    [Fact]
    public void Generate_ShouldSurviveUrlEncoding_Unchanged()
    {
        // The property that actually matters, stated directly: the token is
        // its own escaped form, so no hop in the chain from receipt link to
        // request can alter it. The old encoding failed this - '+' escapes to
        // "%2B", and decodes back as a space wherever some hop forgets.
        string token = SecureToken.Generate();

        Assert.Equal(token, WebUtility.UrlEncode(token));
    }

    [Fact]
    public void Hash_ShouldStayStandardBase64_SoStoredHashesRemainValid()
    {
        // Deliberately NOT url-safe, unlike Generate. Hash output is what sits
        // in refresh_tokens/booking_management_tokens and is only ever
        // compared server-side. Re-encoding it would invalidate every stored
        // hash - every session logged out, every outstanding management link
        // dead - so this pins it against being "made consistent" later.
        string hash = SecureToken.Hash("a-known-token");

        Assert.Equal(Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("a-known-token"))), hash);
    }

    [Fact]
    public void Hash_ShouldProduceTheSameHash_ForTheSameToken()
    {
        string token = SecureToken.Generate();

        Assert.Equal(SecureToken.Hash(token), SecureToken.Hash(token));
    }

    [Fact]
    public void Hash_ShouldNotCollide_AcrossManyDistinctTokens()
    {
        HashSet<string> hashes = Enumerable.Range(0, 1000)
            .Select(_ => SecureToken.Hash(SecureToken.Generate()))
            .ToHashSet();

        Assert.Equal(1000, hashes.Count);
    }

    [Fact]
    public void Hash_ShouldNotReturnTheRawToken()
    {
        string token = SecureToken.Generate();

        Assert.NotEqual(token, SecureToken.Hash(token));
    }
}
