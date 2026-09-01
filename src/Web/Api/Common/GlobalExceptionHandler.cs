using Api.Serialization;
using BuildingBlocks.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
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
///     exception messages or stack traces for unexpected failures. That rule
///     has no exceptions by design: a status code and a client-visible message
///     are part of the API contract, so the code that knows a failure is the
///     caller's fault states it by throwing an AppException subtype. Matching
///     on BCL exception types here can only guess, and guessing wrong turns a
///     library's internal error into a 400 quoting its message.
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
            // NOTE: there is deliberately no ArgumentException arm here, and
            // adding one back would reintroduce a real defect. It read as
            // "Guard.Against.* is how handlers reject bad input, so map its
            // exception family to 400" - but the switch cannot tell a guard
            // clause apart from an ArgumentException thrown inside Npgsql,
            // System.Text.Json, or any other library. Every one of those
            // became a 400 (a bug reported to the caller as their mistake)
            // carrying ex.Message verbatim to the client, in production,
            // where BuildUnhandledProblem is careful never to. The BCL also
            // appends "(Parameter 'GuestCount')" and, for
            // ArgumentOutOfRangeException, the rejected value - so an
            // internal argument name was part of the public contract.
            //
            // A carve-out for ArgumentNullException used to sit above that
            // arm, on the reasoning that a null reaching a domain factory
            // means a validator gap - a bug, not bad input. That reasoning
            // was right and was never specific to null: it applies just as
            // well to every other guard in an entity or value object. The
            // three handler sites that genuinely validated caller input
            // (HoldAvailabilityHandler) now throw ValidationException
            // directly, so bad input is declared where it is known rather
            // than inferred from an exception type here.
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

        // problem's declared type is the base ProblemDetails, so writing
        // it through the generic WriteAsJsonAsync(problem, ct) overload
        // would serialize only base members - silently dropping
        // ValidationProblemDetails.Errors. Branching on the runtime type
        // and passing its own source-generated JsonTypeInfo keeps Errors
        // in the response and avoids reflection.
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
        // Keys camelCased here, once, rather than at each throw site.
        // ValidationProblemDetails.Errors is a Dictionary<string, string[]>,
        // and PropertyNamingPolicy governs declared property names, never
        // dictionary keys - so a handler's nameof(request.CheckIn) reaches the
        // wire as "CheckIn" while FastEndpoints' own FluentValidation failure
        // on the same field arrives as "checkIn". That gave one API two
        // error-key casings, decided by which layer happened to reject the
        // request, which no client can reasonably branch on.
        //
        // Doing it per-site was tried and didn't hold: two ConfirmBookingHandler
        // throws called ConvertName themselves and every other site forgot.
        // This is the one place every ValidationException converges, so it's
        // the only place the conversion can't be forgotten.
        //
        // Grouped rather than ToDictionary'd: two keys differing only in case
        // would collide once folded, and an exception thrown *inside* the
        // exception handler escapes with no handler left to catch it. Merging
        // their messages is both safer and the more useful answer.
        Dictionary<string, string[]> errors = ex.Errors
            .GroupBy(error => JsonNamingPolicy.CamelCase.ConvertName(error.Key))
            .ToDictionary(group => group.Key, group => group.SelectMany(error => error.Value).ToArray());

        return new ValidationProblemDetails(errors)
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
