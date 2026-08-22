using Bogus;
using Identity.Entities;
using Identity.Features.SignUp;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
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
        SignUpResponse? result = await response.Content.ReadFromJsonAsync<SignUpResponse>(TestContext.Current.CancellationToken);
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
}
