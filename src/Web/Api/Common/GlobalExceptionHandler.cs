using Api.Serialization;
using BuildingBlocks.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
namespace Api.Common;

/// <summary>
///     Catches every exception that escapes a request pipeline (command
///     handlers, EF Core, anything) and turns it into a ProblemDetails
///     response with a consistent shape. AppException subtypes map to their
///     own status code and message; ValidationException specifically becomes
///     a ValidationProblemDetails so multiple field-level errors survive as
///     a field-name -> messages[] map, not a single flattened string.
///     Anything that isn't an AppException is treated as a bug, logged with
///     full detail, and returned as a generic 500 - callers never see raw
///     exception messages or stack traces for unexpected failures.
/// </summary>
public sealed partial class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IHostEnvironment environment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ProblemDetails problem = exception switch
        {
            ValidationException validationEx => BuildValidationProblem(validationEx),
            AppException appEx => BuildProblem(appEx.StatusCode, appEx.Message),
            _ => BuildUnhandledProblem(exception)
        };

        problem.Instance = httpContext.Request.Path;

        if (exception is AppException)
        {
            LogHandledAppException(
                logger,
                exception,
                exception.GetType().Name,
                httpContext.Request.Path,
                exception.Message);
        }
        else
        {
            // Unexpected exceptions get full detail in the logs - this is
            // the only place the real exception message and stack trace
            // should end up. The response to the caller stays generic.
            LogUnhandledException(logger, exception, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/problem+json";

        // problem's declared type is the base ProblemDetails (the switch
        // above's common type), so writing it through the generic
        // WriteAsJsonAsync(problem, ct) overload would serialize only base
        // members - silently dropping ValidationProblemDetails.Errors for
        // every ValidationException thrown from application code. Branching
        // on the actual runtime type and passing its own source-generated
        // JsonTypeInfo keeps Errors in the response and avoids reflection.
        if (problem is ValidationProblemDetails validationProblem)
        {
            await httpContext.Response.WriteAsJsonAsync(
                validationProblem, AppJsonSerializerContext.Default.ValidationProblemDetails, cancellationToken: cancellationToken);
        }
        else
        {
            await httpContext.Response.WriteAsJsonAsync(
                problem, AppJsonSerializerContext.Default.ProblemDetails, cancellationToken: cancellationToken);
        }

        return true;
    }

    private static ValidationProblemDetails BuildValidationProblem(ValidationException ex)
    {
        return new ValidationProblemDetails(ex.Errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failed",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1"
        };
    }

    private static ProblemDetails BuildProblem(int statusCode, string detail)
    {
        return new ProblemDetails
        {
            Status = statusCode,
            Title = ReasonPhraseFor(statusCode),
            Detail = detail,
            Type = $"https://tools.ietf.org/html/rfc9110#section-15.{StatusCategoryFragment(statusCode)}"
        };
    }

    private ProblemDetails BuildUnhandledProblem(Exception exception)
    {
        return new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred",
            // Only leak the real exception message in Development - in any
            // other environment this stays generic so internals (SQL,
            // connection strings, stack details) never reach the client.
            Detail = environment.IsDevelopment()
                ? exception.Message
                : "An unexpected error occurred. Please try again later.",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1"
        };
    }

    private static string ReasonPhraseFor(int statusCode)
    {
        return statusCode switch
        {
            StatusCodes.Status400BadRequest => "Bad request",
            StatusCodes.Status401Unauthorized => "Unauthorized",
            StatusCodes.Status403Forbidden => "Forbidden",
            StatusCodes.Status404NotFound => "Not found",
            StatusCodes.Status409Conflict => "Conflict",
            _ => "An error occurred"
        };
    }

    private static string StatusCategoryFragment(int statusCode)
    {
        return statusCode switch
        {
            StatusCodes.Status400BadRequest => "5.1",
            StatusCodes.Status401Unauthorized => "5.2",
            StatusCodes.Status403Forbidden => "5.4",
            StatusCodes.Status404NotFound => "5.5",
            StatusCodes.Status409Conflict => "5.10",
            _ => "6.1"
        };
    }

    [LoggerMessage(LogLevel.Warning, "{ExceptionType} handled for {Path}: {Message}")]
    private static partial void LogHandledAppException(
        ILogger logger,
        Exception exception,
        string exceptionType,
        PathString path,
        string message);

    [LoggerMessage(LogLevel.Error, "Unhandled exception for {Path}")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception, PathString path);
}
