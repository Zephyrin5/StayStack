using Bogus;
using Identity.Entities;
using Identity.Features.RefreshToken;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Auth;

// Proves the fix for the refresh-token rotation race: ValidateRefreshToken
// used to be SELECT -> check IsRevoked -> UPDATE, three separate steps, so
// two concurrent callers presenting the same still-valid token could both
// observe IsRevoked == false and both rotate it. Consumption is now a
// single conditional UPDATE (AuthTokenProvider.ValidateRefreshToken), so
// only one of any number of concurrent callers can win.
[Collection("Integration Tests")]
public class RefreshTokenConcurrencyTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly Faker _faker = new Faker();

    private async Task SeedUserAsync(string email, string password)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email
        };

        IdentityResult result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, "Failed to seed test user.");
    }

    [Fact]
    public async Task RefreshToken_ConcurrentRequestsWithSameToken_ExactlyOneSucceeds()
    {
        // Arrange
        string email = _faker.Internet.Email();
        string password = $"P@{_faker.Internet.Password()}!";
        await SeedUserAsync(email, password);

        HttpClient signInClient = factory.CreateClient();
        HttpResponseMessage signInResponse = await signInClient.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);

        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult?.RefreshToken);

        RefreshTokenRequest refreshRequest = new RefreshTokenRequest { RefreshToken = signInResult.RefreshToken };

        // Act: fire 10 concurrent rotations of the exact same refresh token,
        // each on its own HttpClient/connection.
        const int concurrentRequests = 10;
        Task<HttpResponseMessage>[] tasks = [.. Enumerable.Range(0, concurrentRequests)
            .Select(_ => factory.CreateClient().PostAsJsonAsync("/api/auth/refresh-token", refreshRequest, TestContext.Current.CancellationToken))];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert: exactly one request rotated the token successfully - the
        // race this test targets would otherwise let more than one through.
        int successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        int unauthorizedCount = responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized);

        Assert.Equal(1, successCount);
        Assert.Equal(concurrentRequests - 1, unauthorizedCount);
    }
}
