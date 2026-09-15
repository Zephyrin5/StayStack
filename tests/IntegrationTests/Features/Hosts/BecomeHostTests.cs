using Hosts.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Bogus;
using Hosts;
using Hosts.Contracts;
using Identity;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.RefreshToken;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Hosts;

[Collection("Integration Tests")]
public class BecomeHostTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    [Fact]
    public async Task BecomeHost_ConcurrentRequestsFromTheSameUser_ProduceOneHostAndNoServerError()
    {
        // A double-click, or a client retrying a request still in flight. Each
        // attempt registers its own Host and links it; the loser's link fails the
        // user row's concurrency check, and its scope rolls its Host back with it.
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();
        string businessName = UniqueBusinessName();

        // Separate clients so these genuinely overlap rather than queueing on
        // one connection - same reasoning as the other concurrency tests here.
        Task<HttpResponseMessage>[] attempts =
        [
            factory.CreateClient().SendAsync(CreateBecomeHostRequest(accessToken, businessName), TestContext.Current.CancellationToken),
            factory.CreateClient().SendAsync(CreateBecomeHostRequest(accessToken, businessName), TestContext.Current.CancellationToken)
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(attempts);

        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.InternalServerError);
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        // The user is linked to a Host that exists, and the loser left no second Host.
        using IServiceScope scope = factory.Services.CreateScope();
        AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
        ApplicationUser user = await identity.Users.AsNoTracking()
            .SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);

        Assert.NotNull(user.HostId);
        Assert.True(await HostExistsAsync(user.HostId!.Value));
        Assert.Equal(1, await HostCountAsync(businessName));
    }

    [Fact]
    public async Task BecomeHost_WhenLinkingFailsAfterTheHostIsRegistered_LeavesNoHostBehind()
    {
        // A failed host link must not leave an orphaned Host row. The failure lands
        // after RegisterHostAsync has written the Host, inside the same transaction
        // as the link that never happens.
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();
        string businessName = UniqueBusinessName();

        using WebApplicationFactory<Program> host = HostWithRegistrarFailingAfterRegister(failures: 1);
        HttpResponseMessage response = await host.CreateClient().SendAsync(
            CreateBecomeHostRequest(accessToken, businessName), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await HostCountAsync(businessName));

        using IServiceScope scope = factory.Services.CreateScope();
        ApplicationUser user = await scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>().Users.AsNoTracking()
            .SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        Assert.Null(user.HostId);
    }

    [Fact]
    public async Task BecomeHost_AfterAFailedAttempt_CanBeRetried()
    {
        // A failed attempt must not lock the user out: nothing it wrote survives,
        // so AlreadyAHostException has nothing to fire on.
        (_, string accessToken) = await SeedAndSignInUserAsync();

        using WebApplicationFactory<Program> host = HostWithRegistrarFailingAfterRegister(failures: 1);
        HttpClient client = host.CreateClient();

        HttpResponseMessage failed = await client.SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        HttpResponseMessage retry = await client.SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    private WebApplicationFactory<Program> HostWithRegistrarFailingAfterRegister(int failures)
    {
        FailureBudget budget = new FailureBudget(failures);

        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IHostRegistrar));
                services.Remove(original);
                services.Add(new ServiceDescriptor(
                    typeof(IHostRegistrar),
                    sp => new FailAfterRegister(
                        (IHostRegistrar)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), budget),
                    original.Lifetime));
            }));
    }

    private sealed class FailureBudget(int failures)
    {
        private int _remaining = failures;

        public bool Take() => Interlocked.Decrement(ref _remaining) >= 0;
    }

    // Registers for real, then fails - the Host row exists in the transaction when
    // the request fails.
    private sealed class FailAfterRegister(IHostRegistrar inner, FailureBudget budget) : IHostRegistrar
    {
        public async Task RegisterHostAsync(
            Guid hostId, string businessName, string contactEmail, string? contactPhone, CancellationToken cancellationToken)
        {
            await inner.RegisterHostAsync(hostId, businessName, contactEmail, contactPhone, cancellationToken);
            if (budget.Take())
            {
                throw new InvalidOperationException("Injected failure after the Host was registered.");
            }
        }

        public Task DeleteAsync(Guid hostId, CancellationToken cancellationToken) =>
            inner.DeleteAsync(hostId, cancellationToken);
    }

    [Fact]
    public async Task BecomeHost_WhoseCommitLosesItsAcknowledgement_ReturnsTheHostItMade_WithAUsableRefreshToken()
    {
        // The scope's commit lands and its acknowledgement is lost, so the whole
        // scope runs again. The retry must recognise its own link rather than answer
        // "already a host", create no second Host, and return the refresh token whose
        // hash the first attempt stored.
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();
        string businessName = UniqueBusinessName();

        CommitFault<AppIdentityDbContext> lostAck = CommitFaults.FailAfterCommit<AppIdentityDbContext>((context, ct) =>
            CommitFaults.CommittedRowExistsAsync(context,
                "SELECT 1 FROM users WHERE id = @userId AND host_id IS NOT NULL", "userId", userId, ct));
        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);
        HttpClient client = host.CreateClient();

        HttpResponseMessage response = await client.SendAsync(
            CreateBecomeHostRequest(accessToken, businessName), TestContext.Current.CancellationToken);

        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the scope's commit.");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        BecomeHostResponse? result = await response.Content.ReadFromJsonAsync<BecomeHostResponse>(
            TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.RefreshToken);

        Assert.Equal(1, await HostCountAsync(businessName));

        HttpResponseMessage refreshed = await client.PostAsJsonAsync("/api/auth/refresh-token",
            new RefreshTokenRequest { RefreshToken = result.RefreshToken }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    [Fact]
    public async Task RegisterHost_WhenAnArchivedHostOccupiesTheId_DoesNotCreateASecond()
    {
        // An archived Host still occupies the primary key. Registration has to see
        // it, or the insert collides and the unique-violation catch adopts it
        // anyway - implicitly, through an exception, instead of by an explicit check.
        Guid hostId = Guid.CreateVersion7();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IHostRegistrar>().RegisterHostAsync(
                hostId, "Archived Co", "archived@example.com", null, TestContext.Current.CancellationToken);

            AppHostsDbContext hosts = scope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
            Host host = await hosts.Hosts.SingleAsync(h => h.Id == hostId, TestContext.Current.CancellationToken);
            host.Archive(DateTimeOffset.UtcNow, null);
            await hosts.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IHostRegistrar>().RegisterHostAsync(
                hostId, "Archived Co", "archived@example.com", null, TestContext.Current.CancellationToken);
        }

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppHostsDbContext assertHosts = assertScope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        int count = await assertHosts.Hosts
            .IgnoreQueryFilters()
            .CountAsync(h => h.Id == hostId, TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RegisterHost_CalledRepeatedlyWithTheSameId_CreatesExactlyOneHost()
    {
        // The id is supplied by the caller, so calling again with it is a no-op
        // rather than a second Host.
        Guid hostId = Guid.CreateVersion7();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IHostRegistrar>().RegisterHostAsync(
                hostId, "Retried Co", "retried@example.com", null, TestContext.Current.CancellationToken);
        }

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppHostsDbContext hosts = assertScope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        int count = await hosts.Hosts
            .IgnoreQueryFilters()
            .CountAsync(h => h.Id == hostId, TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    private async Task<bool> HostExistsAsync(Guid hostId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppHostsDbContext hosts = scope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        return await hosts.Hosts
            .IgnoreQueryFilters()
            .AnyAsync(h => h.Id == hostId, TestContext.Current.CancellationToken);
    }

    private async Task<int> HostCountAsync(string businessName)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppHostsDbContext hosts = scope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        return await hosts.Hosts
            .IgnoreQueryFilters()
            .CountAsync(h => h.BusinessName == businessName, TestContext.Current.CancellationToken);
    }

    private string UniqueBusinessName() => $"{_faker.Company.CompanyName()} {Guid.NewGuid():N}";

    private async Task<(Guid UserId, string AccessToken)> SeedAndSignInUserAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email
        };

        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test user.");

        HttpResponseMessage signInResponse = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);

        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult?.AccessToken);

        return (user.Id, signInResult.AccessToken);
    }

    private static BecomeHostRequest CreateValidRequest(string businessName = "Test Business")
    {
        return new BecomeHostRequest
        {
            BusinessName = businessName,
            ContactEmail = "contact@test-business.com"
        };
    }

    private static HttpRequestMessage CreateBecomeHostRequest(string accessToken, string businessName = "Test Business")
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/hosts/become")
        {
            Content = JsonContent.Create(CreateValidRequest(businessName))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    [Fact]
    public async Task BecomeHost_ShouldReturn200_AndLinkHostToUser_WhenRequestIsValid()
    {
        // Arrange
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        BecomeHostResponse? result = await response.Content.ReadFromJsonAsync<BecomeHostResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.HostId);
        Assert.Contains("Host", result.Roles);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser? persistedUser = await userManager.FindByIdAsync(userId.ToString());
        Assert.NotNull(persistedUser);
        Assert.Equal(result.HostId, persistedUser.HostId);

        AppHostsDbContext hostsDb = scope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        bool hostExists = await hostsDb.Hosts.AnyAsync(h => h.Id == result.HostId, TestContext.Current.CancellationToken);
        Assert.True(hostExists);
    }

    [Fact]
    public async Task BecomeHost_ShouldReturn409_WhenAccountAlreadyHasHost()
    {
        // Arrange: first call succeeds and links a host
        (_, string accessToken) = await SeedAndSignInUserAsync();
        HttpResponseMessage firstResponse = await _client.SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Act: same account tries again
        HttpResponseMessage response = await _client.SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task BecomeHost_ShouldReturn401_WhenNotAuthenticated()
    {
        // Act: no Authorization header attached
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/hosts/become", CreateValidRequest(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BecomeHost_ShouldNotLeaveOrphanedHostOrDanglingHostId_WhenRoleAssignmentFails()
    {
        // Arrange
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();

        using IServiceScope preScope = factory.Services.CreateScope();
        AppHostsDbContext preHostsDb = preScope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        int hostCountBefore = await preHostsDb.Hosts.CountAsync(TestContext.Current.CancellationToken);

        // Force the AddToRoleAsync step inside BecomeHostHandler to fail by
        // removing the "Host" role it depends on, rather than mocking
        // UserManager - this exercises the rollback of the link and the Host it
        // had just created against a real database.
        using (IServiceScope seedScope = factory.Services.CreateScope())
        {
            AppIdentityDbContext identityDb = seedScope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
            var hostRole = await identityDb.Roles.SingleAsync(
                r => r.Name == "Host", TestContext.Current.CancellationToken);
            identityDb.Roles.Remove(hostRole);
            await identityDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            // Act
            HttpResponseMessage response = await _client.SendAsync(
                CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);

            // Assert: the call must not report success while actually
            // leaving the account without the role it asked for.
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            using IServiceScope assertScope = factory.Services.CreateScope();
            var userManager = assertScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser? persistedUser = await userManager.FindByIdAsync(userId.ToString());
            Assert.NotNull(persistedUser);
            Assert.Null(persistedUser.HostId); // rolled back, not left dangling

            AppHostsDbContext hostsDb = assertScope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
            int hostCountAfter = await hostsDb.Hosts.CountAsync(TestContext.Current.CancellationToken);
            Assert.Equal(hostCountBefore, hostCountAfter); // no orphaned Host row survived
        }
        finally
        {
            // Restore the role so later tests in this shared-container
            // collection aren't affected by this test's setup.
            using IServiceScope cleanupScope = factory.Services.CreateScope();
            AppIdentityDbContext identityDb = cleanupScope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
            bool roleStillMissing = !await identityDb.Roles.AnyAsync(
                r => r.Name == "Host", TestContext.Current.CancellationToken);
            if (roleStillMissing)
            {
                identityDb.Roles.Add(new IdentityRole<Guid>
                {
                    Id = Guid.Parse("01a00be7-ddff-7598-bfaa-256e7999a546"),
                    Name = "Host",
                    NormalizedName = "HOST"
                });
                await identityDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }
    }
}
