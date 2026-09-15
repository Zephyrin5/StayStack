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
            // NOTE: there is deliberately no ArgumentException arm. The switch
            // cannot tell a guard clause from an ArgumentException thrown inside
            // Npgsql, System.Text.Json or any other library, so one would turn
            // those bugs into 400s blaming the caller, carrying ex.Message -
            // including "(Parameter 'GuestCount')" and, for
            // ArgumentOutOfRangeException, the rejected value - to the client in
            // production, where BuildUnhandledProblem never does.
            //
            // A guard firing in an entity or value object means a validator gap:
            // a bug, not bad input. Handlers that validate caller input throw
            // ValidationException directly, so bad input is declared where it is
            // known rather than inferred from an exception type here.
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

    private ProblemDetails BuildProblem(int statusCode, string detail)
    {
        (string title, string type) = DescribeStatus(statusCode);

        return new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = type
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

    /// <summary>
    ///     The title and RFC reference for one status code.
    ///     <para>
    ///         One switch returning both, so a code cannot be added to one and not
    ///         the other - a 429 with a 500's title and RFC section says server
    ///         error while the status line says back off.
    ///     </para>
    ///     <para>
    ///         429's reference is RFC 6585, not RFC 9110 - 9110 §15.5
    ///         enumerates 400-417, 421, 422 and 426 and does not define 429 at
    ///         all. Hence whole URIs here, not fragments of one rfc9110 template.
    ///     </para>
    /// </summary>
    private (string Title, string Type) DescribeStatus(int statusCode)
    {
        return statusCode switch
        {
            StatusCodes.Status400BadRequest => ("Bad request", Rfc9110("15.5.1")),
            StatusCodes.Status401Unauthorized => ("Unauthorized", Rfc9110("15.5.2")),
            StatusCodes.Status403Forbidden => ("Forbidden", Rfc9110("15.5.4")),
            StatusCodes.Status404NotFound => ("Not found", Rfc9110("15.5.5")),
            StatusCodes.Status409Conflict => ("Conflict", Rfc9110("15.5.10")),
            StatusCodes.Status429TooManyRequests => ("Too many requests", "https://tools.ietf.org/html/rfc6585#section-4"),
            // Loud in Development, generic everywhere else. An unmapped code
            // is a bug in this switch rather than a runtime condition, and the
            // only thing worse than an unhelpful response body is one nobody
            // notices - which is what a silent fallback guarantees, since every
            // unmapped code produces a plausible-looking 500-shaped answer.
            _ when environment.IsDevelopment() => throw new InvalidOperationException(
                $"Status code {statusCode} has no entry in {nameof(DescribeStatus)}. Add its title and RFC reference; " +
                "the fallback would otherwise report it as a generic server error to clients."),
            _ => ("An error occurred", Rfc9110("15.6.1"))
        };
    }

    private static string Rfc9110(string section) => $"https://tools.ietf.org/html/rfc9110#section-{section}";

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
