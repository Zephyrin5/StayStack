using Bogus;
using Hosts;
using Identity;
using Identity.Entities;
using Identity.Features.BecomeHost;
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

    private static BecomeHostRequest CreateValidRequest()
    {
        return new BecomeHostRequest
        {
            BusinessName = "Test Business",
            ContactEmail = "contact@test-business.com"
        };
    }

    private static HttpRequestMessage CreateBecomeHostRequest(string accessToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/hosts/become")
        {
            Content = JsonContent.Create(CreateValidRequest())
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
        // UserManager - this exercises the handler's actual compensating
        // rollback (undo the HostId link, delete the Host record it had
        // just created) against a real database, instead of just trusting
        // the code comment that describes it.
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
