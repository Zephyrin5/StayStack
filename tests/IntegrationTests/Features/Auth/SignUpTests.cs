using Bogus;
using Identity;
using Persistence;
using Identity.Entities;
using Identity.Features.Common;
using Identity.Features.RefreshToken;
using Identity.Features.SignUp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
namespace IntegrationTests.Features.Auth;

[Collection("Integration Tests")]
public class SignUpTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private static SignUpRequest CreateValidRequest(string email)
    {
        return new SignUpRequest
        {
            Email = email,
            Password = "correct-horse-battery-staple",
            ConfirmPassword = "correct-horse-battery-staple"
        };
    }

    [Fact]
    public async Task SignUp_ShouldReturn200_AndGrantCustomerRole_WhenRequestIsValid()
    {
        // Arrange
        string email = _faker.Internet.Email();

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/auth/register", CreateValidRequest(email), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SignUpResponse? result = await response.Content.ReadFromJsonAsync<SignUpResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(email, result.Email);
        Assert.Contains("Customer", result.Roles);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser? persistedUser = await userManager.FindByEmailAsync(email);
        Assert.NotNull(persistedUser);
    }

    [Fact]
    public async Task SignUp_ShouldReturn409_WhenEmailAlreadyInUse()
    {
        // Arrange: register once successfully
        string email = _faker.Internet.Email();
        HttpResponseMessage firstResponse = await _client.PostAsJsonAsync(
            "/api/auth/register", CreateValidRequest(email), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Act: register again with the same email
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/auth/register", CreateValidRequest(email), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task SignUp_ShouldReturn400_WhenPasswordAndConfirmPasswordDontMatch()
    {
        // Arrange
        SignUpRequest request = CreateValidRequest(_faker.Internet.Email()) with { ConfirmPassword = "a-completely-different-password" };

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/auth/register", request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SignUp_ShouldReturn400_WhenPasswordFailsIdentityPolicy_NotJustFluentValidation()
    {
        // A password that clears FluentValidation's own MinimumLength(12)
        // check but fails ASP.NET Core Identity's own RequiredUniqueChars
        // policy (see IdentityServicesRegistration) - this exercises
        // SignUpHandler's `if (!createResult.Succeeded)` branch specifically,
        // not just request-shape validation happening before the handler
        // even runs.
        SignUpRequest request = CreateValidRequest(_faker.Internet.Email()) with
        {
            Password = "aaaaaaaaaaaa",
            ConfirmPassword = "aaaaaaaaaaaa"
        };

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/auth/register", request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Fails the last write registration performs.
    private sealed class FailAfterTheAccountExists(IAuthTokenProvider inner) : IAuthTokenProvider
    {
        public string GenerateJwtToken(ApplicationUser user, IList<string> roles) =>
            inner.GenerateJwtToken(user, roles);

        public string GenerateScopedToken(string audience, IEnumerable<Claim> claims, TimeSpan lifetime) =>
            inner.GenerateScopedToken(audience, claims, lifetime);

        public Task<RefreshTokenValidationResult> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken) =>
            inner.ValidateRefreshToken(refreshToken, cancellationToken);

        public Task<string> GenerateRefreshToken(
            Guid userId, Guid? familyId, Guid? parentTokenId, IssuedRefreshToken token, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Refresh token storage is unavailable.");

        public Task<Guid?> FindCommittedRotationAsync(
            string presentedToken, Guid replacementId, CancellationToken cancellationToken) =>
            inner.FindCommittedRotationAsync(presentedToken, replacementId, cancellationToken);

        public Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) =>
            inner.RevokeRefreshTokenAsync(refreshToken, cancellationToken);
    }

    [Fact]
    public async Task SignUp_ThatFailsPartWayThrough_LeavesNoAccountBehind()
    {
        // Two things at once, and the first is the load-bearing one.
        //
        // Registration is one transaction, which works only if UserManager
        // saves through the same scoped AppIdentityDbContext the transaction
        // was opened on. If it resolved its own context the writes would commit
        // beside the transaction and the rollback would remove nothing - so
        // this asserts the assumption rather than a comment asserting it.
        //
        // Second: the failure is injected at GenerateRefreshToken, the last
        // write. A failure there must not leave a registered account behind
        // while telling the caller registration failed - they could neither
        // register again (the email is taken) nor sign in (no token).
        string email = _faker.Internet.Email();

        HttpClient client = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IAuthTokenProvider));
                services.Remove(original);
                services.AddScoped<IAuthTokenProvider>(sp => new FailAfterTheAccountExists(
                    (IAuthTokenProvider)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)));
            })).CreateClient();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/register", CreateValidRequest(email), TestContext.Current.CancellationToken);

        // Assert - the caller is told it failed...
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // ...and nothing was left behind for them to trip over. Checked
        // through UserManager rather than a raw query so it sees exactly what
        // a second registration attempt would see.
        using IServiceScope scope = factory.Services.CreateScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        Assert.Null(await userManager.FindByEmailAsync(email));
    }

    [Fact]
    public async Task SignUp_AfterAFailedAttempt_CanUseTheSameEmail()
    {
        // The consequence that matters to a person. The assertion above is
        // about a row; this is about whether they can get an account.
        string email = _faker.Internet.Email();

        HttpClient failing = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IAuthTokenProvider));
                services.Remove(original);
                services.AddScoped<IAuthTokenProvider>(sp => new FailAfterTheAccountExists(
                    (IAuthTokenProvider)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)));
            })).CreateClient();

        await failing.PostAsJsonAsync("/api/auth/register", CreateValidRequest(email), TestContext.Current.CancellationToken);

        // Act - the ordinary pipeline, same address.
        HttpResponseMessage retry = await _client.PostAsJsonAsync(
            "/api/auth/register", CreateValidRequest(email), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task SignUp_WhoseCommitLosesItsAcknowledgement_ReturnsTheAccountItCreated()
    {
        // Registration commits the account, its role and its first refresh token
        // together. A retry after a lost acknowledgement must not run
        // CreateAsync again for an account that now exists and answer a
        // validation error to someone who just registered.
        string email = _faker.Internet.Email();
        // After the commit that registered this email, matched on the tracked
        // account.
        CommitFault<AppIdentityDbContext> lostAck = CommitFaults.FailAfterCommit<AppIdentityDbContext>(context =>
            context.ChangeTracker.Entries<ApplicationUser>().Any(e => e.Entity.Email == email));

        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);

        // Act
        HttpResponseMessage response = await host.CreateClient().PostAsJsonAsync(
            "/api/auth/register", CreateValidRequest(email), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the registration.");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SignUpResponse? result = await response.Content
            .ReadFromJsonAsync<SignUpResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.RefreshToken);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppIdentityDbContext db = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();

            ApplicationUser account = await db.Users.AsNoTracking()
                .SingleAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            Assert.Equal(result.Id, account.Id);
            Assert.Contains("Customer", result.Roles);
        }

        // The refresh token handed back is the one that committed - proven by
        // using it, not by its shape.
        HttpResponseMessage refreshed = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh-token",
            new RefreshTokenRequest { RefreshToken = result.RefreshToken }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }
}
