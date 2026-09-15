using Identity;
using Identity.Entities;
using Identity.Exceptions;
using Identity.Features.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
namespace IntegrationTests.Features.Auth;

[Collection("Integration Tests")]
public class AuthTokenProviderTests(IntegrationTestWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> SeedUserAsync(IServiceScope scope)
    {
        string email = $"{Guid.NewGuid():N}@example.com";
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user)).Succeeded);
        return user.Id;
    }

    [Fact]
    public async Task GenerateRefreshToken_PersistsOnlyTheTokensHash()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        Guid userId = await SeedUserAsync(scope);

        string rawToken = await scope.ServiceProvider.GetRequiredService<IAuthTokenProvider>()
            .GenerateRefreshToken(userId, familyId: null, parentTokenId: null, IssuedRefreshToken.New(), Ct);

        RefreshToken stored = await scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>().RefreshTokens
            .AsNoTracking().SingleAsync(t => t.UserId == userId, Ct);

        Assert.Equal(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))), stored.TokenHash);
    }

    [Fact]
    public async Task ValidateRefreshToken_ConsumesTheTokenOnFirstUse()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        Guid userId = await SeedUserAsync(scope);
        IAuthTokenProvider provider = scope.ServiceProvider.GetRequiredService<IAuthTokenProvider>();
        string rawToken = await provider.GenerateRefreshToken(userId, null, null, IssuedRefreshToken.New(), Ct);

        RefreshTokenValidationResult result = await provider.ValidateRefreshToken(rawToken, Ct);

        Assert.Equal(userId, result.UserId);
        Assert.True((await scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>().RefreshTokens
            .AsNoTracking().SingleAsync(t => t.UserId == userId, Ct)).IsRevoked);
    }

    [Fact]
    public async Task ValidateRefreshToken_RejectsAnUnknownToken()
    {
        using IServiceScope scope = factory.Services.CreateScope();

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() =>
            scope.ServiceProvider.GetRequiredService<IAuthTokenProvider>().ValidateRefreshToken("invalid_token", Ct));
    }
}
