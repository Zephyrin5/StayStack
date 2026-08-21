namespace BuildingBlocks.Identity;

/// <summary>
///     Resolves who is making the current change, for CreatedBy/ModifiedBy,
///     and what they're permitted to act as. Same shape as
///     ICurrentLanguageProvider - a thin, injectable abstraction so
///     handlers stay decoupled from HttpContext and testable without a
///     real request pipeline.
/// </summary>
public interface ICurrentUserProvider
{
    // Null for system-driven changes (background jobs, seed data,
    // anonymous/guest-checkout writes) - not every change has a user
    // behind it, and CreatedBy/ModifiedBy are already nullable to reflect
    // that honestly rather than forcing a placeholder value.
    Guid? UserId { get; }

    // Present only once BecomeHost has completed - see the "host_id"
    // claim in AuthTokenProvider. Null means "this caller isn't linked to
    // a host", not "unknown" - IHostAuthorizationService.RequireHostId()
    // is what turns that into a real 403 where it matters.
    Guid? HostId { get; }

    IReadOnlyCollection<string> Roles { get; }
}
