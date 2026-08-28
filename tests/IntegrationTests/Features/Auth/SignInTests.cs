using Bogus;
using Identity.Entities;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Auth;

[Collection("Integration Tests")]
public class SignInIntegrationTests(IntegrationTestWebApplicationFactory factory)
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
    public async Task SignIn_ShouldReturn200_AndTokenPair_WhenCredentialsAreValid()
    {
        // Arrange
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";
        await SeedUserAsync(email, password);

        SignInRequest request = new SignInRequest
        {
            Email = email,
            Password = password
        };

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));
        Assert.Equal(email, result.Email);
    }

    [Fact]
    public async Task SignIn_ShouldReturn400_WhenEmailIsInvalidFormat()
    {
        // Tests SignInRequestValidator + GlobalExceptionHandler in 1 step
        SignInRequest request = new SignInRequest
        {
            Email = _faker.Random.Word(), // Non-email string
            Password = $"P@1{_faker.Internet.Password()}!"
        };

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SignIn_ShouldReturn401_WhenPasswordIsIncorrect()
    {
        // Tests SignInHandler + SignInManager + GlobalExceptionHandler in 1 step
        string email = _faker.Internet.Email();
        string correctPassword = $"P@1{_faker.Internet.Password()}!";
        string wrongPassword = $"P@2{_faker.Internet.Password()}!";

        await SeedUserAsync(email, correctPassword);

        SignInRequest request = new SignInRequest
        {
            Email = email,
            Password = wrongPassword
        };

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SignIn_ShouldLockAccount_AfterFiveFailedAttempts()
    {
        // IdentityServicesRegistration configures MaxFailedAccessAttempts =
        // 5 - lockoutOnFailure: true in SignInHandler is what actually arms
        // it (previously false, so failed attempts never counted at all).
        string email = _faker.Internet.Email();
        string correctPassword = $"P@1{_faker.Internet.Password()}!";
        string wrongPassword = $"P@2{_faker.Internet.Password()}!";
        await SeedUserAsync(email, correctPassword);

        for (int i = 0; i < 5; i++)
        {
            HttpResponseMessage failedAttempt = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
            {
                Email = email,
                Password = wrongPassword
            });
            Assert.Equal(HttpStatusCode.Unauthorized, failedAttempt.StatusCode);
        }

        // The 6th attempt uses the CORRECT password - if lockout is truly
        // armed, this still fails, proving the account is locked rather
        // than just repeatedly rejecting a wrong password.
        HttpResponseMessage lockedAttempt = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = correctPassword
        });

        Assert.Equal(HttpStatusCode.Unauthorized, lockedAttempt.StatusCode);
    }

    [Fact]
    public async Task SignIn_LockedOutResponse_ShouldBeIndistinguishableFromWrongPassword()
    {
        // See docs/adr/0016: a distinguishable "account locked" response is
        // itself an enumeration oracle (only an account that exists can
        // ever be locked out), so SignInHandler deliberately maps
        // IsLockedOut to the exact same InvalidCredentialsException as any
        // other failure. Asserts the actual response bodies match, not just
        // both being 401 - a different Detail message would still leak it.
        string email = _faker.Internet.Email();
        string correctPassword = $"P@1{_faker.Internet.Password()}!";
        string wrongPassword = $"P@2{_faker.Internet.Password()}!";
        await SeedUserAsync(email, correctPassword);

        HttpResponseMessage ordinaryWrongPassword = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = wrongPassword
        });
        string ordinaryBody = await ordinaryWrongPassword.Content.ReadAsStringAsync();

        for (int i = 0; i < 4; i++)
        {
            await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest { Email = email, Password = wrongPassword });
        }

        // Now locked out - even the correct password fails.
        HttpResponseMessage lockedOut = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = correctPassword
        });
        string lockedOutBody = await lockedOut.Content.ReadAsStringAsync();

        Assert.Equal(ordinaryWrongPassword.StatusCode, lockedOut.StatusCode);
        Assert.Equal(ordinaryBody, lockedOutBody);
    }
}
