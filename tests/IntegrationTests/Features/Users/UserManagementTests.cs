using Bogus;
using BuildingBlocks.Pagination;
using Identity.Entities;
using Identity.Features.AssignRole;
using Identity.Features.GetUsers;
using Identity.Features.RemoveRole;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
// "Users" not "Identity" - a sibling test namespace literally named
// IntegrationTests.Features.Identity shadows the real top-level Identity
// module namespace for every OTHER file's `using Identity....` directive
// in this same compilation (C# resolves a using-directive's leading
// identifier against enclosing namespace declarations first, and
// namespace declaration spaces merge across the whole compilation, not
// per-file) - confirmed by this exact collision breaking
// PricingRuleHandlerTests.cs's unrelated `using Identity.Features.BecomeHost;`
// the first time this file was named IntegrationTests.Features.Identity.
namespace IntegrationTests.Features.Users;

[Collection("Integration Tests")]
public class UserManagementTests(IntegrationTestWebApplicationFactory factory)
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

    private async Task<(Guid UserId, string AccessToken)> SeedPlainUserAsync()
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
        return (user.Id, result.AccessToken);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string accessToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    [Fact]
    public async Task GetUsers_ShouldReturnOnlyMatchingRole_WhenRoleFilterProvided()
    {
        string adminToken = await SignInAsSeededAdminAsync();
        (Guid userId, _) = await SeedPlainUserAsync();

        HttpResponseMessage assignResponse = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/users/{userId}/roles/PropertyStaff", adminToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, assignResponse.StatusCode);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Get, "/api/users?role=PropertyStaff&pageSize=100", adminToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PagedResponse<UserSummary>? result =
            await response.Content.ReadFromJsonAsync<PagedResponse<UserSummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Contains(result.Items, u => u.UserId == userId);
        Assert.All(result.Items, u => Assert.Contains("PropertyStaff", u.Roles));
    }

    [Fact]
    public async Task GetUsers_ShouldReturn403_ForNonAdminCaller()
    {
        (_, string accessToken) = await SeedPlainUserAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Get, "/api/users", accessToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AssignRole_ShouldReturn200_AndAddRole_ForAdmin()
    {
        string adminToken = await SignInAsSeededAdminAsync();
        (Guid userId, _) = await SeedPlainUserAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/users/{userId}/roles/PropertyStaff", adminToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssignRoleResponse? result =
            await response.Content.ReadFromJsonAsync<AssignRoleResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Contains("PropertyStaff", result.Roles);
    }

    [Fact]
    public async Task AssignRole_ShouldReturn400_ForUnknownRoleName()
    {
        string adminToken = await SignInAsSeededAdminAsync();
        (Guid userId, _) = await SeedPlainUserAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/users/{userId}/roles/NotARealRole", adminToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AssignRole_ShouldReturn404_ForNonExistentUser()
    {
        string adminToken = await SignInAsSeededAdminAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/users/{Guid.NewGuid()}/roles/PropertyStaff", adminToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AssignRole_ShouldReturn403_ForNonAdminCaller()
    {
        (Guid userId, string accessToken) = await SeedPlainUserAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/users/{userId}/roles/PropertyStaff", accessToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RemoveRole_ShouldReturn200_AndRemoveRole_ForAdmin()
    {
        string adminToken = await SignInAsSeededAdminAsync();
        (Guid userId, _) = await SeedPlainUserAsync();

        await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/users/{userId}/roles/PropertyStaff", adminToken),
            TestContext.Current.CancellationToken);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/users/{userId}/roles/PropertyStaff", adminToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RemoveRoleResponse? result =
            await response.Content.ReadFromJsonAsync<RemoveRoleResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.DoesNotContain("PropertyStaff", result.Roles);
    }

    [Fact]
    public async Task RemoveRole_ShouldReturn400_WhenRemovingTheLastAdministrator()
    {
        // The seeded admin@staystack.com is the only Administrator in a
        // fresh test database - removing it would leave zero, which is
        // exactly the invariant this endpoint has to protect.
        string adminToken = await SignInAsSeededAdminAsync();

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser admin = (await userManager.FindByEmailAsync("admin@staystack.com"))!;

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/users/{admin.Id}/roles/Administrator", adminToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RemoveRole_ShouldSucceed_WhenAnotherAdministratorRemains()
    {
        // Deliberately never touches the seeded admin@staystack.com's own
        // Administrator role - other tests in this collection depend on
        // it staying intact. Grants the role to two fresh users instead,
        // so removing it from one of them still leaves 2+ Administrators
        // (the seeded one plus the other fresh one).
        string adminToken = await SignInAsSeededAdminAsync();
        (Guid firstExtraAdminId, _) = await SeedPlainUserAsync();
        (Guid secondExtraAdminId, _) = await SeedPlainUserAsync();

        await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/users/{firstExtraAdminId}/roles/Administrator", adminToken),
            TestContext.Current.CancellationToken);
        await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/users/{secondExtraAdminId}/roles/Administrator", adminToken),
            TestContext.Current.CancellationToken);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/users/{firstExtraAdminId}/roles/Administrator", adminToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Full cleanup, not just the one call under test - secondExtraAdminId
        // is still an Administrator at this point. Leaving it would make
        // admin@staystack.com no longer "the last remaining Administrator"
        // for the rest of this shared-database test collection, silently
        // breaking RemoveRole_ShouldReturn400_WhenRemovingTheLastAdministrator
        // (and every other test that signs in as the seeded admin expecting
        // Administrator privileges) depending on run order - this is exactly
        // the failure mode that motivated this comment.
        await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/users/{secondExtraAdminId}/roles/Administrator", adminToken),
            TestContext.Current.CancellationToken);
    }
}
