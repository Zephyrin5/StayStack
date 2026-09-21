using Bogus;
using Identity.Configurations;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
namespace IntegrationTests.Features.Auth;

// docs/adr/0030. A signature and an expiry say a token was ours and is not stale; they say nothing
// about whether the roles inside it are still the account's.
//
// Each case makes an authenticated request before the change, which is what puts the account's old
// stamp in the cache - so these also pin the eviction, not only the check. Both halves were broken
// to watch them fail: unwiring OnTokenValidated fails all four, and removing the eviction from
// RemoveRoleHandler fails ARoleTakenAway on its own, leaving the rest green.
[Collection("Integration Tests")]
public class SecurityStampTests(IntegrationTestWebApplicationFactory factory)
{
    private const string AnyAuthenticatedEndpoint = "/api/bookings/mine";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    [Fact]
    public async Task ARoleTakenAway_StopsTheTokenThatStillClaimsIt()
    {
        string adminToken = await SignInAsAdminAsync();
        (Guid userId, string password, string email) = await SeedUserAsync();

        await AssignAsync(adminToken, userId, "PropertyStaff");

        // Issued after the grant, so it carries the role and the stamp that went with it.
        string token = await SignInAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(AnyAuthenticatedEndpoint, token));

        await RemoveAsync(adminToken, userId, "PropertyStaff");

        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(AnyAuthenticatedEndpoint, token));
    }

    [Fact]
    public async Task ARoleGranted_StopsTheTokenIssuedBeforeIt()
    {
        string adminToken = await SignInAsAdminAsync();
        (Guid userId, string password, string email) = await SeedUserAsync();

        string token = await SignInAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(AnyAuthenticatedEndpoint, token));

        await AssignAsync(adminToken, userId, "PropertyStaff");

        // Nothing in the old token is false; it simply predates the account as it now is, and the
        // check has no way to tell that apart from a token that has gone stale in the other
        // direction. Retiring both is the trade this makes.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(AnyAuthenticatedEndpoint, token));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(AnyAuthenticatedEndpoint, await SignInAsync(email, password)));
    }

    [Fact]
    public async Task BecomingAHost_RetiresTheTokenThatPredatesTheLink()
    {
        (_, string password, string email) = await SeedUserAsync();
        string token = await SignInAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(AnyAuthenticatedEndpoint, token));

        using HttpRequestMessage become = new HttpRequestMessage(HttpMethod.Post, "/api/hosts/become")
        {
            Content = JsonContent.Create(new BecomeHostRequest
            {
                BusinessName = _faker.Company.CompanyName(),
                ContactEmail = _faker.Internet.Email(),
                ContactPhone = "+96512345678"
            }, options: TestJsonOptions.Default)
        };
        become.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage becameHost = await _client.SendAsync(become, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, becameHost.StatusCode);

        BecomeHostResponse? result = await becameHost.Content
            .ReadFromJsonAsync<BecomeHostResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.AccessToken);

        // The token the caller arrived with has no host_id and never will; the one they leave with
        // does. Without this, the host endpoints stayed shut until the access token expired.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(AnyAuthenticatedEndpoint, token));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(AnyAuthenticatedEndpoint, result.AccessToken));
    }

    [Fact]
    public async Task ATokenWithoutTheStampClaim_IsRejected()
    {
        (Guid userId, _, string email) = await SeedUserAsync();

        // Correctly signed, correctly addressed, not expired, and unreadable by this check - which
        // is the only reason it is refused. A check that let it through would be skippable by
        // omitting one claim.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(AnyAuthenticatedEndpoint, SignWithoutAStamp(userId, email)));
    }

    private string SignWithoutAStamp(Guid userId, string email)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthTokenConfiguration settings = scope.ServiceProvider
            .GetRequiredService<IOptions<AuthTokenConfiguration>>().Value;

        SecurityTokenDescriptor descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, email),
                new Claim(JwtRegisteredClaimNames.Email, email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ]),
            Expires = DateTime.UtcNow.AddMinutes(settings.AccessTokenLifespanInMinutes),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Key)), SecurityAlgorithms.HmacSha256),
            Issuer = settings.Issuer,
            Audience = settings.Audience
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private async Task<(Guid UserId, string Password, string Email)> SeedUserAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = new ApplicationUser { Id = Guid.CreateVersion7(), Email = email, UserName = email };
        Assert.True((await userManager.CreateAsync(user, password)).Succeeded);

        return (user.Id, password, email);
    }

    private async Task<string> SignInAsync(string email, string password)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in",
            new SignInRequest { Email = email, Password = password }, TestContext.Current.CancellationToken);

        SignInResponse? result = await response.Content
            .ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private Task<string> SignInAsAdminAsync() =>
        SignInAsync(IntegrationTestAdmin.Email, IntegrationTestAdmin.Password);

    private async Task<HttpStatusCode> GetAsync(string path, string accessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private Task AssignAsync(string adminToken, Guid userId, string role) =>
        SendAsync(HttpMethod.Post, $"/api/users/{userId}/roles/{role}", adminToken);

    private Task RemoveAsync(string adminToken, Guid userId, string role) =>
        SendAsync(HttpMethod.Delete, $"/api/users/{userId}/roles/{role}", adminToken);

    private async Task SendAsync(HttpMethod method, string path, string accessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
