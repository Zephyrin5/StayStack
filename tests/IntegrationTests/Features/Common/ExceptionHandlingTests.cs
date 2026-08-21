using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Common;

public class ExceptionHandlingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
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

        ValidationProblemDetails? problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
    }
}
