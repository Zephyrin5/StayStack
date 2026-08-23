using BuildingBlocks.Exceptions;
using System.Net;
namespace Identity.Exceptions;

/// <summary>
///     A registration attempt used an email address that already has an
///     account. Deliberately does NOT distinguish "email exists" from any
///     other validation failure in a way that could be used to enumerate
///     registered emails faster than the normal registration flow already
///     allows - this is a 409 with a plain message, not a hint about which
///     specific field matched.
/// </summary>
public sealed class EmailAlreadyInUseException()
    : AppException("An account with this email already exists.", (int)HttpStatusCode.Conflict);
