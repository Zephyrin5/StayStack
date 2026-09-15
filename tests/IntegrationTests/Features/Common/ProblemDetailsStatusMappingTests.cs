using Api.Common;
using Bookings.Exceptions;
using BuildingBlocks.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
namespace IntegrationTests.Features.Common;

// GlobalExceptionHandler built a ProblemDetails from two switches keyed on the
// same status code, and they had to agree. They stopped agreeing for any code
// in neither - TooManyActiveHoldsException correctly carries 429 and got the
// title "An error occurred" and the RFC section for 500, so the body claimed a
// server error while the status line told the client to back off.
//
// Driven against the handler directly rather than through a request. Provoking
// a real 429 over HTTP means exhausting the per-network hold cap, and the test
// host gives every request the same loopback address - so the cap is shared
// with whatever else is running and the test would be a coin flip.
public class ProblemDetailsStatusMappingTests
{
    private static async Task<ProblemDetails> HandleAsync(Exception exception, IHostEnvironment environment)
    {
        DefaultHttpContext context = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() }
        };
        context.Request.Path = "/api/availability/holds";

        IExceptionHandler handler = new GlobalExceptionHandler(
            NullLogger<GlobalExceptionHandler>.Instance, environment);

        Assert.True(await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken));

        context.Response.Body.Position = 0;
        ProblemDetails? problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(
            context.Response.Body, TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(problem);
        return problem;
    }

    private sealed class Environment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task A429_IsDescribedAsARateLimit_NotAsAServerError()
    {
        ProblemDetails problem = await HandleAsync(
            new TooManyActiveHoldsException(), new Environment("Production"));

        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.Status);
        Assert.Equal("Too many requests", problem.Title);

        // RFC 6585, not 9110. 9110 section 15.5 enumerates 400-417, 421, 422
        // and 426 and does not define 429 at all.
        Assert.Equal("https://tools.ietf.org/html/rfc6585#section-4", problem.Type);
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound, "Not found", "15.5.5")]
    [InlineData(StatusCodes.Status409Conflict, "Conflict", "15.5.10")]
    public async Task AMappedStatus_KeepsItsTitleAndReference(int statusCode, string title, string section)
    {
        // The codes that already worked, pinned so folding the two switches
        // into one did not quietly change any of them.
        ProblemDetails problem = await HandleAsync(
            new FixedStatusException(statusCode), new Environment("Production"));

        Assert.Equal(statusCode, problem.Status);
        Assert.Equal(title, problem.Title);
        Assert.Equal($"https://tools.ietf.org/html/rfc9110#section-{section}", problem.Type);
    }

    [Fact]
    public async Task AnUnmappedStatus_ThrowsInDevelopment_SoTheNextOneIsNotSilent()
    {
        // The whole hazard is that the fallback produces a plausible answer.
        // Nobody reports "the title said An error occurred", so the next code
        // added without an entry here would go the same way 429 did.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HandleAsync(new FixedStatusException(StatusCodes.Status418ImATeapot), new Environment("Development")).AsTask());
    }

    [Fact]
    public async Task AnUnmappedStatus_StillAnswersInProduction()
    {
        // Loud in Development, never a 500 in front of a real user: throwing
        // inside the exception handler leaves nothing to catch it.
        ProblemDetails problem = await HandleAsync(
            new FixedStatusException(StatusCodes.Status418ImATeapot), new Environment("Production"));

        Assert.Equal(StatusCodes.Status418ImATeapot, problem.Status);
        Assert.Equal("An error occurred", problem.Title);
    }

    private sealed class FixedStatusException(int statusCode)
        : AppException("Something the caller did.", statusCode);
}

file static class TaskExtensions
{
    public static Task AsTask(this Task task) => task;
}
