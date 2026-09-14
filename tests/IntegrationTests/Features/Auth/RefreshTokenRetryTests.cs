using Identity;
using Identity.Entities;
using Identity.Features.RefreshToken;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Persistence;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Auth;

// A refresh is consume-once, and that is what made a lost acknowledgement fatal.
//
// The first attempt revoked the presented token and committed its replacement.
// The retry then presented the same token, found it already revoked - which is
// exactly what genuine reuse looks like - and revoked the whole family,
// including the replacement it had just committed. A transient blip on the
// commit's acknowledgement logged the user out. Probed before the fix: 401
// "reuse detected", 0 of 2 tokens left live.
//
// The retry cannot tell its own earlier attempt from an attacker by the token
// alone; that is the point of the design. What distinguishes them is the
// replacement's identity, which the handler now chooses once, outside the
// retry, and which an attacker replaying a stolen token never has.
[Collection("Integration Tests")]
public class RefreshTokenRetryTests(IntegrationTestWebApplicationFactory factory)
{
    // After the commit, and only a commit carrying a new refresh token for the
    // target user. Identity registers no audit interceptor to substitute, so
    // this is attached to the context's options directly.
    private sealed class LoseTheAckOnFirstRotationFor(Guid userId) : DbTransactionInterceptor
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) > 0;

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is AppIdentityDbContext context
                && context.ChangeTracker.Entries<RefreshToken>()
                    .Any(e => e.Entity.UserId == userId && e.Entity.ParentTokenId != null)
                && Interlocked.Increment(ref _fired) == 1)
            {
                throw new PostgresException("simulated lost acknowledgement after commit", "ERROR", "ERROR", "40001");
            }

            return Task.CompletedTask;
        }
    }

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

    private WebApplicationFactory<Program> HostLosingTheFirstRotationAck(LoseTheAckOnFirstRotationFor interceptor)
    {
        string connection = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("AppConnection")!;

        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                services.AddDbContext<AppIdentityDbContext>(options =>
                {
                    options.ConfigureStayStackDefaults(connection, "identity", false);
                    options.AddInterceptors(interceptor);
                });
            }));
    }

    private Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/auth/refresh-token",
            new RefreshTokenRequest { RefreshToken = refreshToken }, TestContext.Current.CancellationToken);

    private async Task<List<RefreshToken>> TokensForAsync(Guid userId)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>().RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARefreshWhoseCommitLosesItsAcknowledgement_ReturnsTheRotationItCommitted()
    {
        (Guid userId, string original) = await SignInAsync();

        LoseTheAckOnFirstRotationFor interceptor = new LoseTheAckOnFirstRotationFor(userId);
        using WebApplicationFactory<Program> host = HostLosingTheFirstRotationAck(interceptor);

        // Act
        HttpResponseMessage response = await RefreshAsync(host.CreateClient(), original);

        // Assert - a rotation, not a revocation.
        Assert.True(interceptor.Fired, "The lost acknowledgement never reached the rotation.");
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

        LoseTheAckOnFirstRotationFor interceptor = new LoseTheAckOnFirstRotationFor(userId);
        using WebApplicationFactory<Program> host = HostLosingTheFirstRotationAck(interceptor);

        HttpResponseMessage recovered = await RefreshAsync(host.CreateClient(), original);
        Assert.True(interceptor.Fired);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);

        // Act - the original token, presented again.
        HttpResponseMessage replay = await RefreshAsync(factory.CreateClient(), original);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.All(await TokensForAsync(userId), t => Assert.True(t.IsRevoked));
    }
}
