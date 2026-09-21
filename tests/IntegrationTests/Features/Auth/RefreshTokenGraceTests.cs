using Bogus;
using BuildingBlocks.Security;
using Identity;
using Identity.Configurations;
using Identity.Entities;
using Identity.Features.RefreshToken;
using Identity.Features.SignIn;
using Identity.Features.SignOut;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Auth;

// A token that is revoked and replaced is what a replayed stolen token looks like, and also what
// the loser of a concurrent rotation looks like. These pin where the line between them is
// (docs/adr/0009): inside RotationReuseGraceSeconds the benign reading wins and the family lives;
// outside it, or revoked by something other than a rotation, reuse detection runs.
//
// The clock is moved by writing revoked_at rather than by a fake TimeProvider: the host under test
// is the real one, and what decides this is the value in the row.
[Collection(AuthCollection.Name)]
public class RefreshTokenGraceTests(AuthFixture factory)
{
    private readonly Faker _faker = new Faker();

    [Fact]
    public async Task AConcurrentBurst_LeavesTheWinnersTokenUsable()
    {
        // The assertion the status-code count alone cannot make. Every loser reaches a token that is
        // revoked and replaced; before the grace they each revoked the family on the way out, which
        // killed the replacement the winner had just been handed - so the burst "succeeded" and the
        // user was signed out anyway.
        string refreshToken = await SignInAsync();

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => RefreshAsync(refreshToken)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));

        HttpResponseMessage winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        RefreshTokenResponse? rotated = await winner.Content
            .ReadFromJsonAsync<RefreshTokenResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(rotated?.RefreshToken);

        HttpResponseMessage next = await RefreshAsync(rotated.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task ATokenRotatedInsideTheGrace_IsRefusedAndLeavesTheFamilyLive()
    {
        (string consumed, Guid familyId) = await SignInAndRotateAsync();
        await BackdateRevocationAsync(consumed, TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(consumed)).StatusCode);
        Assert.Equal(0, await RevokedInFamilyAsync(familyId, exceptThe: consumed));
    }

    [Fact]
    public async Task ATokenRotatedBeforeTheGrace_RevokesTheFamily()
    {
        // Past the window there is nothing left to distinguish a duplicate from a replay, and the
        // replay is the one worth acting on.
        (string consumed, Guid familyId) = await SignInAndRotateAsync();
        await BackdateRevocationAsync(consumed, GraceSeconds() + TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(consumed)).StatusCode);
        Assert.Equal(1, await RevokedInFamilyAsync(familyId, exceptThe: consumed));
    }

    [Fact]
    public async Task ATokenRevokedBySignOut_StillRevokesTheFamily()
    {
        // Sign-out leaves replaced_by_token_id null, which is the whole of what separates it from a
        // rotation. A token presented after sign-out was never superseded, so somebody kept it.
        string refreshToken = await SignInAsync();
        Guid familyId = await FamilyOfAsync(refreshToken);

        HttpResponseMessage signedOut = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/sign-out", new SignOutRequest { RefreshToken = refreshToken }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, signedOut.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(refreshToken)).StatusCode);

        // Nothing else is in this family, so the assertion is that the path ran at all: it reaches
        // RevokeFamilyAsync rather than the grace's early return.
        using IServiceScope scope = factory.Services.CreateScope();
        IdentityDb dbContext = scope.ServiceProvider.GetRequiredService<IdentityDb>();
        List<RefreshToken> family = await dbContext.RefreshTokens.AsNoTracking()
            .Where(t => t.FamilyId == familyId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(family, token => Assert.True(token.IsRevoked));
        Assert.All(family, token => Assert.Null(token.ReplacedByTokenId));
    }

    private TimeSpan GraceSeconds()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return TimeSpan.FromSeconds(scope.ServiceProvider
            .GetRequiredService<IOptions<AuthTokenConfiguration>>().Value.RotationReuseGraceSeconds);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        factory.CreateClient().PostAsJsonAsync(
            "/api/auth/refresh-token",
            new RefreshTokenRequest { RefreshToken = refreshToken },
            TestContext.Current.CancellationToken);

    private async Task<string> SignInAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser user = new ApplicationUser { Id = Guid.CreateVersion7(), Email = email, UserName = email };
            Assert.True((await userManager.CreateAsync(user, password)).Succeeded);
        }

        HttpResponseMessage response = await factory.CreateClient().PostAsJsonAsync("/api/auth/sign-in",
            new SignInRequest { Email = email, Password = password }, TestContext.Current.CancellationToken);

        SignInResponse? result = await response.Content
            .ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(result?.RefreshToken);
        return result.RefreshToken;
    }

    /// <summary>Signs in and rotates once, returning the token the rotation consumed.</summary>
    private async Task<(string Consumed, Guid FamilyId)> SignInAndRotateAsync()
    {
        string consumed = await SignInAsync();
        Guid familyId = await FamilyOfAsync(consumed);

        HttpResponseMessage rotated = await RefreshAsync(consumed);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        return (consumed, familyId);
    }

    private async Task BackdateRevocationAsync(string plaintext, TimeSpan by)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IdentityDb dbContext = scope.ServiceProvider.GetRequiredService<IdentityDb>();
        string hash = SecureToken.Hash(plaintext);

        int updated = await dbContext.RefreshTokens
            .Where(t => t.TokenHash == hash)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow - by),
                TestContext.Current.CancellationToken);

        Assert.Equal(1, updated);
    }

    private async Task<Guid> FamilyOfAsync(string plaintext)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IdentityDb dbContext = scope.ServiceProvider.GetRequiredService<IdentityDb>();
        string hash = SecureToken.Hash(plaintext);

        return await dbContext.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => t.FamilyId)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     How many tokens in the family are revoked, not counting the one the presenter already
    ///     consumed - so this counts what reuse detection took down, and nothing else.
    /// </summary>
    private async Task<int> RevokedInFamilyAsync(Guid familyId, string exceptThe)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IdentityDb dbContext = scope.ServiceProvider.GetRequiredService<IdentityDb>();
        string consumedHash = SecureToken.Hash(exceptThe);

        return await dbContext.RefreshTokens.AsNoTracking()
            .CountAsync(t => t.FamilyId == familyId && t.TokenHash != consumedHash && t.IsRevoked,
                TestContext.Current.CancellationToken);
    }
}
