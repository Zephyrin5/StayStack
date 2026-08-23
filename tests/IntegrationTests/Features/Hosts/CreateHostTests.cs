using Bogus;
using Hosts;
using Hosts.Entities;
using Hosts.Features.CreateHost;
using Identity.Entities;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Hosts;

[Collection("Integration Tests")]
public class CreateHostTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private async Task<string> SignInAsSeededAdminAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = "admin@staystack.com",
            Password = "1234"
        }, TestContext.Current.CancellationToken);

        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private async Task<string> SignInAsPlainCustomerAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test user.");

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);
        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private static HttpRequestMessage CreateHostHttpRequest(CreateHostRequest body, string accessToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/hosts") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    [Fact]
    public async Task CreateHost_ShouldReturn200_AndPersistHost_WithNoLinkedUser_ForAdmin()
    {
        // Arrange
        string adminAccessToken = await SignInAsSeededAdminAsync();
        CreateHostRequest request = new CreateHostRequest
        {
            BusinessName = "Gulf Stays Co.",
            ContactEmail = "contact@gulfstays.example",
            DisplayName = new Dictionary<string, string> { { "en", "Gulf Stays" } }
        };

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            CreateHostHttpRequest(request, adminAccessToken), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateHostResponse? result = await response.Content.ReadFromJsonAsync<CreateHostResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.HostId);

        using IServiceScope scope = factory.Services.CreateScope();
        AppHostsDbContext db = scope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        Host host = await db.Hosts.SingleAsync(h => h.Id == result.HostId, TestContext.Current.CancellationToken);
        Assert.Equal("Gulf Stays Co.", host.BusinessName);
        Assert.Equal("Gulf Stays", host.DisplayName?.Values["en"]);
    }

    [Fact]
    public async Task CreateHost_ShouldReturn401_WhenNotAuthenticated()
    {
        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/hosts", new CreateHostRequest
        {
            BusinessName = "Anonymous Co.",
            ContactEmail = "nobody@example.com"
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateHost_ShouldReturn403_ForAuthenticatedNonAdminCaller()
    {
        // Arrange: a plain, freshly-registered account has no roles at all,
        // let alone Administrator.
        string customerAccessToken = await SignInAsPlainCustomerAsync();

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            CreateHostHttpRequest(new CreateHostRequest
            {
                BusinessName = "Should Not Exist Co.",
                ContactEmail = "nope@example.com"
            }, customerAccessToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
