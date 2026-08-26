using BuildingBlocks.Security;
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
