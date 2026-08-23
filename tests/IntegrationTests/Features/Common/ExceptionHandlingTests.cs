using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Common;

// Was IClassFixture<WebApplicationFactory<Program>> - a bare, unconfigured
// factory that never calls UseEnvironment("Testing"), so it fell back to
// WebApplicationFactory's own default environment. That happened to satisfy
// IsDevelopment(), which picks up AddUserSecrets (see Program.cs) - so this
// only ever passed locally because of real user-secrets already configured
// on this machine from unrelated work earlier, never on a clean CI runner
// with none. Using the shared IntegrationTestWebApplicationFactory instead
// gives it the same "Testing" environment (and appsettings.Testing.json JWT
// key) every other integration test already relies on.
[Collection("Integration Tests")]
public class ExceptionHandlingTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetNonExistentEndpoint_Returns404ProblemDetails()
    {
        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/non-existent-route");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task InvalidPayload_Returns400ValidationProblemDetails()
    {
        // Act: Sending invalid request to an endpoint that triggers validation failure
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", new { Email = "" });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ValidationProblemDetails? problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(TestJsonOptions.Default);
        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
    }
}
