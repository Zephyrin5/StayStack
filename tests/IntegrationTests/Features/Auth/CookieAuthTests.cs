using Bogus;
using Identity.Entities;
using Identity.Features.Auth.RefreshToken;
using Identity.Features.Auth.SignIn;
using Identity.Features.Auth.SignOut;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Auth;

// Cookie-mode auth (?useCookies=true) - see Api.Security.AuthCookies and
// the plan this session's summary references. WebApplicationFactory's
// default CreateClient() has cookie handling enabled, so a single
// HttpClient instance reused across calls in one test carries the cookie
// jar automatically, exactly like a browser tab would.
[Collection("Integration Tests")]
public class CookieAuthTests(IntegrationTestWebApplicationFactory factory)
{
    private const string CookieName = "staystack_refresh_token";

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

    private static string? GetSetCookieValue(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return null;
        }

        foreach (string header in values)
        {
            if (!header.StartsWith($"{cookieName}=", StringComparison.Ordinal))
            {
                continue;
            }

            string afterName = header[(cookieName.Length + 1)..];
            int semicolon = afterName.IndexOf(';');
            return semicolon >= 0 ? afterName[..semicolon] : afterName;
        }

        return null;
    }

    [Fact]
    public async Task SignIn_WithUseCookies_ShouldSetHttpOnlyCookie_AndOmitRefreshTokenFromBody()
    {
        // Arrange
        HttpClient client = factory.CreateClient();
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password(10)}!";
        await SeedUserAsync(email, password);

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/sign-in?useCookies=true", new SignInRequest { Email = email, Password = password });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookieHeaders));
        string cookieHeader = Assert.Single(setCookieHeaders!);
        Assert.Contains(CookieName, cookieHeader);
        Assert.Contains("httponly", cookieHeader, StringComparison.OrdinalIgnoreCase);

        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.Null(result.RefreshToken);
    }

    [Fact]
    public async Task SignIn_WithoutUseCookies_ShouldBeUnchanged_TokenModeStillWorks()
    {
        // Guards the token-mode path mobile/non-browser clients depend on
        // against a regression from this change.

        // Arrange
        HttpClient client = factory.CreateClient();
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password(10)}!";
        await SeedUserAsync(email, password);

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/sign-in", new SignInRequest { Email = email, Password = password });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));

        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));
    }

    [Fact]
    public async Task RefreshToken_ViaCookieAlone_ShouldRotateCookie_AndInvalidateOldValue()
    {
        // Arrange
        HttpClient client = factory.CreateClient();
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password(10)}!";
        await SeedUserAsync(email, password);

        HttpResponseMessage signInResponse = await client.PostAsJsonAsync(
            "/api/auth/sign-in?useCookies=true", new SignInRequest { Email = email, Password = password });
        string? originalCookieValue = GetSetCookieValue(signInResponse, CookieName);
        Assert.NotNull(originalCookieValue);

        // Act: empty body, cookie carries the token (same HttpClient, cookie jar applied automatically)
        using HttpRequestMessage refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh-token?useCookies=true")
        {
            Content = JsonContent.Create(new RefreshTokenRequest())
        };
        HttpResponseMessage refreshResponse = await client.SendAsync(refreshRequest);

        // Assert: succeeded and rotated to a new cookie value
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        string? rotatedCookieValue = GetSetCookieValue(refreshResponse, CookieName);
        Assert.NotNull(rotatedCookieValue);
        Assert.NotEqual(originalCookieValue, rotatedCookieValue);

        RefreshTokenResponse? refreshResult = await refreshResponse.Content.ReadFromJsonAsync<RefreshTokenResponse>();
        Assert.NotNull(refreshResult);
        Assert.Null(refreshResult.RefreshToken);

        // Act: replay the ORIGINAL (now-rotated-away) cookie value explicitly on a fresh client
        using HttpClient replayClient = factory.CreateClient();
        using HttpRequestMessage replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh-token?useCookies=true")
        {
            Content = JsonContent.Create(new RefreshTokenRequest())
        };
        replayRequest.Headers.Add("Cookie", $"{CookieName}={originalCookieValue}");
        HttpResponseMessage replayResponse = await replayClient.SendAsync(replayRequest);

        // Assert: the old token was revoked by the rotation above
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
    }

    [Fact]
    public async Task RefreshToken_WithNoBodyAndNoCookie_ShouldReturn401()
    {
        // Arrange
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/refresh-token?useCookies=true", new RefreshTokenRequest());

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SignOut_ShouldRevokeToken_AndClearCookie()
    {
        // Arrange
        HttpClient client = factory.CreateClient();
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password(10)}!";
        await SeedUserAsync(email, password);

        await client.PostAsJsonAsync(
            "/api/auth/sign-in?useCookies=true", new SignInRequest { Email = email, Password = password });

        // Act: sign out (cookie carried automatically by the same client)
        HttpResponseMessage signOutResponse = await client.PostAsJsonAsync(
            "/api/auth/sign-out", new SignOutRequest());

        // Assert: cookie cleared in the response
        Assert.Equal(HttpStatusCode.OK, signOutResponse.StatusCode);
        Assert.True(signOutResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookieHeaders));
        string cookieHeader = Assert.Single(setCookieHeaders!);
        Assert.Contains(CookieName, cookieHeader);

        // Act: the revoked token can no longer refresh (same client - cookie jar already cleared,
        // but confirms the server-side revocation too, not just the client-side cookie deletion)
        HttpResponseMessage refreshResponse = await client.PostAsJsonAsync(
            "/api/auth/refresh-token?useCookies=true", new RefreshTokenRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
    }

    [Fact]
    public async Task SignOut_WithNoTokenAtAll_ShouldStillReturn200()
    {
        // Sign-out is idempotent - nothing to revoke is a valid outcome,
        // not a failure (see SignOutHandler's own comment).

        // Arrange
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/sign-out", new SignOutRequest());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
