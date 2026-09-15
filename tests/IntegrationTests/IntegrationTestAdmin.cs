using Identity.Entities;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests;

/// <summary>
///     The administrator these tests sign in as.
///     <para>
///         Created by the test host, not seeded by the schema: a seeded
///         administrator would give every deployment that runs migrations a
///         known-credential account holding the Administrator role.
///     </para>
///     <para>
///         The password is generated per run rather than being a constant, so
///         there is no fixed string to leak into a fixture file, a log, or a
///         habit. It exists in this process's memory and in a database that is
///         thrown away with the container.
///     </para>
/// </summary>
public static class IntegrationTestAdmin
{
    public const string Email = "integration-test-admin@staystack.test";

    public const string RoleName = "Administrator";

    /// <summary>
    ///     Regenerated per test run. Long and random so a run cannot pass by
    ///     guessing it, which would defeat the point of testing an
    ///     authorization boundary.
    /// </summary>
    public static readonly string Password = $"P@1{Guid.NewGuid():N}!";

    /// <summary>
    ///     Creates the administrator, if the run has not already. Called once
    ///     from the factory's initialization, after migrations - the roles
    ///     themselves are still seeded by RoleConfiguration, which is
    ///     reference data rather than a credential.
    /// </summary>
    public static async Task EnsureCreatedAsync(IServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        if (await userManager.FindByEmailAsync(Email) is not null)
        {
            return;
        }

        ApplicationUser admin = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            Email = Email,
            UserName = Email,
            EmailConfirmed = true
        };

        IdentityResult created = await userManager.CreateAsync(admin, Password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to create the integration-test administrator: " +
                string.Join(", ", created.Errors.Select(e => e.Description)));
        }

        IdentityResult roleAssigned = await userManager.AddToRoleAsync(admin, RoleName);
        if (!roleAssigned.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to grant the integration-test administrator its role: " +
                string.Join(", ", roleAssigned.Errors.Select(e => e.Description)));
        }
    }

    /// <summary>
    ///     Signs in and returns the access token.
    /// </summary>
    public static async Task<string> SignInAsync(HttpClient client, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = Email,
            Password = Password
        }, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SignInResponse? result =
            await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, cancellationToken);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }
}
