using Microsoft.Extensions.Configuration;
namespace BuildingBlocks.Configuration;

/// <summary>
///     Every setting this application owns lives under one root section, and this is the only place
///     that root's name appears.
///     <para>
///         It answers ownership: ASP.NET Core owns Logging, AllowedHosts and ConnectionStrings,
///         libraries own their keys, and this application owns the rest. Flat, a reader cannot tell
///         which are theirs to change. Not nested by module - BookingLifecycle, RateLimiting and
///         Cookies are each read by two modules, so configuration is grouped by concern.
///         ConnectionStrings stays at the root because GetConnectionString() reads it from there.
///     </para>
/// </summary>
public static class AppConfiguration
{
    public const string RootSection = "App";

    /// <summary>
    ///     Resolves one of this application's own sections. Callers pass the section's own name
    ///     (usually its options type's SectionName), never the prefix.
    ///     <para>
    ///         Sections only: a by-key counterpart existed, and a misspelled path returned null and
    ///         fell through to the call site's default. Binding a section to an options type moves
    ///         that to compile time.
    ///     </para>
    /// </summary>
    public static IConfigurationSection AppSection(this IConfiguration configuration, string name) =>
        configuration.GetSection($"{RootSection}:{name}");
}
