using BuildingBlocks.Exceptions;
using Identity;
using Identity.Configurations;
using Identity.Features.Auth.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Cryptography;
using System.Text;
using Assert = Xunit.Assert;


namespace UnitTests.Features.Auth.Common;

public class AuthTokenProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppIdentityDbContext _dbContext;
    private readonly AuthTokenProvider _sut;

    public AuthTokenProviderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppIdentityDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppIdentityDbContext(options);
        _dbContext.Database.EnsureCreated();

        AuthTokenConfiguration config = new AuthTokenConfiguration
        {
            Key = "SUPER_SECRET_KEY_THAT_IS_AT_LEAST_32_BYTES_LONG!",
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            AccessTokenLifespanInMinutes = 15,
            RefreshTokenLifespanInDays = 7
        };

        var optionsMock = new Mock<IOptions<AuthTokenConfiguration>>();
        optionsMock.Setup(o => o.Value).Returns(config);

        _sut = new AuthTokenProvider(_dbContext, optionsMock.Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GenerateRefreshToken_ShouldPersistHashedToken()
    {
        Guid userId = Guid.NewGuid();

        string rawToken = await _sut.GenerateRefreshToken(userId, CancellationToken.None);

        string expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        Identity.Entities.RefreshToken? storedToken = await _dbContext.RefreshTokens.SingleOrDefaultAsync(rt => rt.UserId == userId);

        Assert.NotNull(storedToken);
        Assert.Equal(expectedHash, storedToken.TokenHash);
    }

    [Fact]
    public async Task ValidateRefreshToken_ShouldRevokeTokenOnFirstUse()
    {
        Guid userId = Guid.NewGuid();
        string rawToken = await _sut.GenerateRefreshToken(userId, CancellationToken.None);

        Guid returnedUserId = await _sut.ValidateRefreshToken(rawToken, CancellationToken.None);

        Assert.Equal(userId, returnedUserId);
        Identity.Entities.RefreshToken storedToken = await _dbContext.RefreshTokens.FirstAsync(rt => rt.UserId == userId);
        Assert.True(storedToken.IsRevoked);
    }

    [Fact]
    public async Task ValidateRefreshToken_ShouldThrowInvalidRefreshTokenException_WhenTokenIsInvalid()
    {
        const string rawToken = "invalid_token";

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() =>
            _sut.ValidateRefreshToken(rawToken, CancellationToken.None)
        );
    }

    [Fact]
    public async Task ValidateRefreshToken_ShouldThrowRefreshTokenReuseDetectedException_WhenTokenIsReused()
    {
        Guid userId = Guid.NewGuid();
        string rawToken = await _sut.GenerateRefreshToken(userId, CancellationToken.None);

        // First use (valid)
        await _sut.ValidateRefreshToken(rawToken, CancellationToken.None);

        // Second use (stolen/reused)
        await Assert.ThrowsAsync<RefreshTokenReuseDetectedException>(() =>
            _sut.ValidateRefreshToken(rawToken, CancellationToken.None)
        );
    }
}
