using Bogus;
using Identity.Entities;
using Identity.Features.RefreshToken;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Auth;

[Collection("Integration Tests")]
public class RefreshTokenTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
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
    public async Task RefreshToken_ShouldReturnNewTokenPair_WhenRefreshTokenIsValid()
    {
        // Arrange: Use Bogus to generate user credentials
        string email = _faker.Internet.Email();
        string password = $"P@{_faker.Internet.Password()}!";
        await SeedUserAsync(email, password);

        HttpResponseMessage signInResponse = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);

        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult);

        RefreshTokenRequest refreshRequest = new RefreshTokenRequest { RefreshToken = signInResult.RefreshToken ?? string.Empty };

        // Act
        HttpResponseMessage response =
            await _client.PostAsJsonAsync("/api/auth/refresh-token", refreshRequest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        RefreshTokenResponse? refreshResult = await response.Content.ReadFromJsonAsync<RefreshTokenResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(refreshResult);
        Assert.NotEmpty(refreshResult.AccessToken);
        Assert.False(string.IsNullOrEmpty(refreshResult.RefreshToken));

        // Verify token rotation yielded a new refresh token
        Assert.NotEqual(signInResult.RefreshToken, refreshResult.RefreshToken);
    }

    [Fact]
    public async Task RefreshToken_ShouldReturn401_WhenTokenIsEmptyAndNoCookiePresent()
    {
        // RefreshToken is optional now (cookie-mode callers send no body -
        // see RefreshTokenRequest's own comment), so an empty/missing
        // token with nothing in the cookie jar either is a 401 "invalid
        // refresh token," not a 400 validation failure - there's no
        // longer a structural rule that rejects this before it reaches
        // the handler.

        // Arrange
        RefreshTokenRequest refreshRequest = new RefreshTokenRequest { RefreshToken = string.Empty };

        // Act
        HttpResponseMessage response =
            await _client.PostAsJsonAsync("/api/auth/refresh-token", refreshRequest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RefreshToken_ShouldReturn401_WhenTokenIsReused()
    {
        // Arrange: Generate user credentials using Bogus
        string email = _faker.Internet.Email();
        string password = $"P@{_faker.Internet.Password()}!";
        await SeedUserAsync(email, password);

        HttpResponseMessage signInResponse = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);

        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult);

        RefreshTokenRequest refreshRequest = new RefreshTokenRequest { RefreshToken = signInResult.RefreshToken ?? string.Empty };

        // First use (revokes old token and issues new pair)
        await _client.PostAsJsonAsync("/api/auth/refresh-token", refreshRequest, TestContext.Current.CancellationToken);

        // Act: Attempt to reuse the revoked refresh token
        HttpResponseMessage response =
            await _client.PostAsJsonAsync("/api/auth/refresh-token", refreshRequest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
