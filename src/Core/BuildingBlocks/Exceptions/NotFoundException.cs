using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     A requested entity does not exist.
/// </summary>
public sealed class NotFoundException(string entityName, object key)
    : AppException($"{entityName} with id '{key}' was not found.", (int)HttpStatusCode.NotFound);
