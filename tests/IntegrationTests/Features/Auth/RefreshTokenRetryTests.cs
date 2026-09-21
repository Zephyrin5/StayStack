using BuildingBlocks.Security;
using Identity;
using Identity.Entities;
using Identity.Features.RefreshToken;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using Persistence;
namespace IntegrationTests.Features.Auth;

// A refresh is consume-once, which makes a lost acknowledgement dangerous. The
// first attempt revokes the presented token and commits its replacement; a
// retry presenting the same token finds it revoked - exactly what genuine reuse
// looks like - and, without recovery, revokes the whole family including the
// replacement it just committed: 401 "reuse detected", the user logged out.
//
// The retry cannot tell its own earlier attempt from an attacker by the token
// alone. What distinguishes them is the replacement's identity, which the
// handler chooses once, outside the retry, and which an attacker replaying a
// stolen token never has.
[Collection(AuthCollection.Name)]
public class RefreshTokenRetryTests(AuthFixture factory)
{
    // After the commit carrying a rotation for this user - a new token with a
    // parent, which a sign-in's token does not have.
    private static CommitFault<AppDbContext> LoseTheAckOnTheRotationFor(Guid userId) =>
        CommitFaults.FailAfterCommit<AppDbContext>(context =>
            context.ChangeTracker.Entries<RefreshToken>()
                .Any(e => e.Entity.UserId == userId && e.Entity.ParentTokenId != null));

    private async Task<(Guid UserId, string RefreshToken)> SignInAsync()
    {
        string email = $"refresh-retry-{Guid.NewGuid():N}@example.com";
        const string password = "P@ssw0rd-refresh-retry-1!";
        Guid userId = Guid.NewGuid();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.CreateAsync(
                new ApplicationUser { Id = userId, Email = email, UserName = email }, password)).Succeeded);
        }

        SignInResponse? signIn = await (await factory.CreateClient().PostAsJsonAsync("/api/auth/sign-in",
                new SignInRequest { Email = email, Password = password }, TestContext.Current.CancellationToken)).Content
            .ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(signIn?.RefreshToken);
        return (userId, signIn.RefreshToken);
    }

    private Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/auth/refresh-token",
            new RefreshTokenRequest { RefreshToken = refreshToken }, TestContext.Current.CancellationToken);

    private async Task<List<RefreshToken>> TokensForAsync(Guid userId)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IdentityDb>().RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARefreshWhoseCommitLosesItsAcknowledgement_ReturnsTheRotationItCommitted()
    {
        (Guid userId, string original) = await SignInAsync();

        CommitFault<AppDbContext> lostAck = LoseTheAckOnTheRotationFor(userId);
        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);

        // Act
        HttpResponseMessage response = await RefreshAsync(host.CreateClient(), original);

        // Assert - a rotation, not a revocation.
        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the rotation.");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        RefreshTokenResponse? rotated = await response.Content
            .ReadFromJsonAsync<RefreshTokenResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(rotated?.RefreshToken);

        // Exactly the one rotation the first attempt committed: the original
        // consumed, one live replacement, nothing revoked as reuse.
        List<RefreshToken> tokens = await TokensForAsync(userId);
        Assert.Equal(2, tokens.Count);
        RefreshToken replacement = Assert.Single(tokens, t => !t.IsRevoked);
        Assert.Equal(replacement.Id, Assert.Single(tokens, t => t.IsRevoked).ReplacedByTokenId);

        // And the token handed back is that committed replacement - proven by
        // using it. A response carrying a token whose row was never written
        // would read as success here and fail at the guest's next refresh.
        HttpResponseMessage next = await RefreshAsync(factory.CreateClient(), rotated.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task ReplayingTheOriginalAfterARecoveredRefresh_IsStillReuse_AndRevokesTheFamily()
    {
        // The recovery must not become a hole in reuse detection. It keys on
        // the replacement id this request chose; a replay is a new request,
        // with a new id, so it has to fall through to the reuse path.
        (Guid userId, string original) = await SignInAsync();

        CommitFault<AppDbContext> lostAck = LoseTheAckOnTheRotationFor(userId);
        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);

        HttpResponseMessage recovered = await RefreshAsync(host.CreateClient(), original);
        Assert.True(lostAck.HasFired);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);

        // Past the rotation grace, so this is a replay rather than the other half of a pair still in
        // flight - which is the only thing reuse detection can tell apart (docs/adr/0009).
        // RefreshTokenGraceTests owns the boundary itself; what is under test here is that the
        // recovery path does not swallow a replay that reaches it.
        await BackdateRevocationAsync(original, TimeSpan.FromMinutes(5));

        // Act - the original token, presented again.
        HttpResponseMessage replay = await RefreshAsync(factory.CreateClient(), original);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.All(await TokensForAsync(userId), t => Assert.True(t.IsRevoked));
    }

    private async Task BackdateRevocationAsync(string plaintext, TimeSpan by)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IdentityDb dbContext = scope.ServiceProvider.GetRequiredService<IdentityDb>();
        string hash = SecureToken.Hash(plaintext);

        Assert.Equal(1, await dbContext.RefreshTokens
            .Where(t => t.TokenHash == hash)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(t => t.RevokedAt, DateTime.UtcNow - by),
                TestContext.Current.CancellationToken));
    }
}
