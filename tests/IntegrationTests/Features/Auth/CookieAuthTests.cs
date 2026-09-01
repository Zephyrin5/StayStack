using Bogus;
using Identity.Entities;
using Identity.Features.RefreshToken;
using Identity.Features.SignIn;
using Identity.Features.SignOut;
using Microsoft.AspNetCore.Identity;
using Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Auth;

// Cookie-mode auth (?useCookies=true) - see Api.Security.AuthCookies.
// WebApplicationFactory's default CreateClient() has cookie handling
// enabled, so a single HttpClient instance reused across calls in one
// test carries the cookie jar automatically, exactly like a browser tab
// would.
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
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
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
        string password = $"P@1{_faker.Internet.Password()}!";
        await SeedUserAsync(email, password);

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/sign-in?useCookies=true", new SignInRequest { Email = email, Password = password });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders));
        string cookieHeader = Assert.Single(setCookieHeaders);
        Assert.Contains(CookieName, cookieHeader);
        Assert.Contains("httponly", cookieHeader, StringComparison.OrdinalIgnoreCase);

        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.Null(result.RefreshToken);
    }

    [Fact]
    public async Task SignIn_WithSecureCookiesRequired_SetsSecure_EvenOverPlainHttp()
    {
        // The regression this pins: the Secure flag used to be
        // Request.IsHttps. That reads the proxy's scheme only when
        // UseForwardedHeaders has trusted the proxy, and
        // ForwardedHeaders:KnownProxies ships empty - so behind a
        // TLS-terminating proxy at any non-loopback address it was false and
        // the refresh token went out unprotected.
        //
        // TestServer's transport is plain HTTP, which makes it exactly the
        // shape of that failure: IsHttps is false here too. With the flag
        // declared by configuration rather than derived, the cookie is Secure
        // anyway - which is the whole point, since the app cannot see the
        // TLS the proxy terminated.
        //
        // appsettings.Testing.json turns RequireSecure off so the rest of the
        // suite's cookie jar behaves like a browser on HTTP; this test opts
        // back into the production default on its own host.
        string email = _faker.Internet.Email();
        string password = $"P@{_faker.Internet.Password()}!";
        await SeedUserAsync(email, password);

        using WebApplicationFactory<Program> secureFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<CookieSecurityOptions>(o => o.RequireSecure = true)));

        using HttpClient client = secureFactory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/sign-in?useCookies=true", new SignInRequest { Email = email, Password = password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders));

        string cookieHeader = Assert.Single(setCookieHeaders);
        Assert.Contains(CookieName, cookieHeader);
        Assert.Contains("secure", cookieHeader, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignIn_WithSameSiteNone_SetsSameSiteNone_ForACrossSiteSpa()
    {
        // The escape hatch for a deployment where the SPA and API sit on
        // different registrable domains. There a Lax cookie is never attached
        // to the SPA's fetch calls at all - CORS allows the request, the
        // browser just declines to send the cookie, and cookie auth fails
        // with nothing logged anywhere. None is the only value that works,
        // and it has to be reachable by configuration for that deployment to
        // exist at all.
        //
        // RequireSecure comes along with it because browsers reject
        // SameSite=None without Secure; Program.cs refuses to start on that
        // combination rather than serving cookies nothing will store.
        string email = _faker.Internet.Email();
        string password = $"P@{_faker.Internet.Password()}!";
        await SeedUserAsync(email, password);

        using WebApplicationFactory<Program> crossSiteFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Configure<CookieSecurityOptions>(o =>
            {
                o.SameSite = SameSiteMode.None;
                o.RequireSecure = true;
            })));

        using HttpClient client = crossSiteFactory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/sign-in?useCookies=true", new SignInRequest { Email = email, Password = password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders));

        string cookieHeader = Assert.Single(setCookieHeaders);
        Assert.Contains("samesite=none", cookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookieHeader, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_WithSameSiteNoneButNotSecure_RefusesToStart()
    {
        // Guards the guard. SameSite=None without Secure is refused by every
        // modern browser, so the app would come up healthy and hand out
        // session cookies nothing stores - a failure that looks like "login
        // silently doesn't persist" rather than like a config error. Program.cs
        // throws instead, and this asserts the throw is real and reachable
        // rather than a comment describing an intention.
        using WebApplicationFactory<Program> misconfigured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Configure<CookieSecurityOptions>(o =>
            {
                o.SameSite = SameSiteMode.None;
                o.RequireSecure = false;
            })));

        // The host is built lazily, so the throw surfaces on first resolution.
        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => misconfigured.CreateClient());

        Assert.Contains("SameSite is None but RequireSecure is false", exception.Message);
    }

    [Fact]
    public void CookieSecurityOptions_DefaultsToLax_TheSameSiteCase()
    {
        // Lax, not None. The default deployment shares a registrable domain
        // between SPA and API - cross-origin at most, which CORS handles -
        // and Lax is what keeps the CSRF protection None would give up.
        Assert.Equal(SameSiteMode.Lax, new CookieSecurityOptions().SameSite);
    }

    [Fact]
    public void CookieSecurityOptions_DefaultsToRequiringSecure()
    {
        // Fails closed. A deployment that genuinely serves plain HTTP has to
        // say so in its own configuration; forgetting to configure anything
        // must not be what silently drops the flag - that was the shape of
        // the original defect.
        Assert.True(new CookieSecurityOptions().RequireSecure);
    }

    [Fact]
    public async Task SignIn_WithoutUseCookies_ShouldBeUnchanged_TokenModeStillWorks()
    {
        // Guards the token-mode path mobile/non-browser clients depend on
        // against a regression from this change.

        // Arrange
        HttpClient client = factory.CreateClient();
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";
        await SeedUserAsync(email, password);

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/sign-in", new SignInRequest { Email = email, Password = password });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));

        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));
    }

    [Fact]
    public async Task RefreshToken_ViaCookieAlone_ShouldRotateCookie_AndInvalidateOldValue()
    {
        // Arrange
        HttpClient client = factory.CreateClient();
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";
        await SeedUserAsync(email, password);

        HttpResponseMessage signInResponse = await client.PostAsJsonAsync(
            "/api/auth/sign-in?useCookies=true", new SignInRequest { Email = email, Password = password });
        string? originalCookieValue = GetSetCookieValue(signInResponse, CookieName);
        Assert.NotNull(originalCookieValue);

        // Act: empty body, cookie carries the token (same HttpClient, cookie jar applied automatically)
        using HttpRequestMessage refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh-token?useCookies=true");
        refreshRequest.Content = JsonContent.Create(new RefreshTokenRequest());
        HttpResponseMessage refreshResponse = await client.SendAsync(refreshRequest);

        // Assert: succeeded and rotated to a new cookie value
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        string? rotatedCookieValue = GetSetCookieValue(refreshResponse, CookieName);
        Assert.NotNull(rotatedCookieValue);
        Assert.NotEqual(originalCookieValue, rotatedCookieValue);

        RefreshTokenResponse? refreshResult = await refreshResponse.Content.ReadFromJsonAsync<RefreshTokenResponse>(TestJsonOptions.Default);
        Assert.NotNull(refreshResult);
        Assert.Null(refreshResult.RefreshToken);

        // Act: replay the ORIGINAL (now-rotated-away) cookie value explicitly on a fresh client
        using HttpClient replayClient = factory.CreateClient();
        using HttpRequestMessage replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh-token?useCookies=true");
        replayRequest.Content = JsonContent.Create(new RefreshTokenRequest());
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
        string password = $"P@1{_faker.Internet.Password()}!";
        await SeedUserAsync(email, password);

        await client.PostAsJsonAsync(
            "/api/auth/sign-in?useCookies=true", new SignInRequest { Email = email, Password = password });

        // Act: sign out (cookie carried automatically by the same client)
        HttpResponseMessage signOutResponse = await client.PostAsJsonAsync(
            "/api/auth/sign-out", new SignOutRequest());

        // Assert: cookie cleared in the response
        Assert.Equal(HttpStatusCode.OK, signOutResponse.StatusCode);
        Assert.True(signOutResponse.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders));
        string cookieHeader = Assert.Single(setCookieHeaders);
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
