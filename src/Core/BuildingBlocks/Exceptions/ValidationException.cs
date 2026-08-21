using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     One or more field-level validation failures. Carries a field name to
///     error-message-array map so multiple errors, and multiple errors per
///     field, both survive to the response - this is what lets the API
///     return every broken field at once instead of one message per request.
/// </summary>
public sealed class ValidationException(IDictionary<string, string[]> errors)
    : AppException("One or more validation errors occurred.", (int)HttpStatusCode.BadRequest)
{

    public ValidationException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] })
    {
    }
    public IDictionary<string, string[]> Errors { get; } = errors;
}
