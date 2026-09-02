using Microsoft.Extensions.Configuration;
namespace BuildingBlocks.Configuration;

/// <summary>
///     Every setting this application owns lives under one root section, and
///     this is the only place that root's name appears.
///     <para>
///         The problem it solves is ownership, not tidiness. Configuration
///         arrives from several parties: ASP.NET Core owns <c>Logging</c>,
///         <c>AllowedHosts</c> and <c>ConnectionStrings</c>; libraries own
///         their own keys; this application owns the rest. Flat, those are
///         indistinguishable - a reader cannot tell which keys are theirs to
///         change without already knowing which ones the framework documents.
///         One prefix answers that at a glance.
///     </para>
///     <para>
///         Deliberately NOT nested by module (<c>App:Bookings:...</c>). Several
///         settings are read by more than one module and would have to be
///         misfiled or split: <c>BookingLifecycle</c> is used by Bookings and
///         Reviews, <c>RateLimiting</c> by Identity and Availability,
///         <c>Cookies</c> by Identity and Availability. Configuration is
///         grouped by concern; the module layout is an implementation detail
///         that several concerns legitimately cut across.
///     </para>
///     <para>
///         <c>ConnectionStrings</c> stays at the root on purpose -
///         <c>IConfiguration.GetConnectionString()</c> reads it from there by
///         definition, and moving it would mean giving up the framework method
///         for a hand-rolled lookup.
///     </para>
/// </summary>
public static class AppConfiguration
{
    public const string RootSection = "App";

    /// <summary>
    ///     Resolves one of this application's own sections. Callers pass the
    ///     section's own name (usually its options type's
    ///     <c>SectionName</c>), never the prefix - so the prefix can change in
    ///     one edit rather than however many <c>GetSection</c> calls exist.
    /// </summary>
    public static IConfigurationSection AppSection(this IConfiguration configuration, string name) =>
        configuration.GetSection($"{RootSection}:{name}");

    /// <summary>
    ///     Reads a single value from one of this application's sections, for
    ///     the cases that want one key rather than a bound object.
    /// </summary>
    public static string? AppValue(this IConfiguration configuration, string path) =>
        configuration[$"{RootSection}:{path}"];
}
