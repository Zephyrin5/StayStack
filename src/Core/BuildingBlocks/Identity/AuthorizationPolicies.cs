namespace BuildingBlocks.Identity;

/// <summary>
///     Central home for the role/policy names referenced from more than one
///     place - endpoint Policies() calls, RoleConfiguration's seed data,
///     BecomeHostHandler's role assignment. Adding a new authorization rule
///     is a new const here plus a matching AddPolicy call in
///     IdentityServicesRegistration, not a new string literal typed out
///     somewhere else that has to match these by coincidence.
/// </summary>
public static class AuthorizationPolicies
{
    public const string Host = "Host";
    public const string Administrator = "Administrator";

    /// <summary>
    ///     Endpoints an Administrator can use on a host's behalf as well as
    ///     a Host acting on their own resources (e.g. CreateUnit) - a
    ///     distinct policy rather than stacking Policies(Host, Administrator)
    ///     on the endpoint, since combining multiple named policies on one
    ///     endpoint is an AND (caller would need both roles at once), not
    ///     the "either role" check this actually needs.
    /// </summary>
    public const string HostOrAdministrator = "HostOrAdministrator";
}
