namespace BuildingBlocks.Exceptions;

/// <summary>
///     Base type for exceptions that should produce a specific, intentional
///     HTTP error response rather than a generic 500. Handlers throw these
///     (or a subtype) when a business rule or precondition fails - the
///     GlobalExceptionHandler in Api maps them to a ProblemDetails response
///     using StatusCode and the exception's own Message as Detail.
/// </summary>
public abstract class AppException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
