using System.ComponentModel.DataAnnotations;
namespace BuildingBlocks.Localization;

/// <summary>
///     Bound from the "Localization" config section (see appsettings.json),
///     same pattern as AuthTokenConfiguration. This is the platform-wide
///     answer to "which language is required" that LocalizedText.Create
///     needs from every caller - Domain/SeedWork deliberately has no
///     opinion on this itself (see LocalizedText's own doc comment), so
///     every module's create/update handlers resolve it from here instead.
/// </summary>
public class LocalizationSettings
{
    public const string SectionName = "Localization";

    // set, not init, and that is load-bearing rather than stylistic. This
    // project sets EnableConfigurationBindingGenerator, and the generated
    // binder assigns properties directly - which it cannot do to an init-only
    // setter outside an object initializer, so it skips them without
    // complaint. Bound against init, both of these silently kept the defaults
    // below. Every other options type in the codebase already uses set; this
    // was the odd one out.
    [Required(AllowEmptyStrings = false)]
    public string DefaultCulture { get; set; } = "en";
    /// <summary>
    ///     No default value, deliberately. The binder appends to a collection
    ///     that already has elements rather than replacing it, so an initializer
    ///     of ["en", "ar"] plus a configured ["ar", "fr"] yields
    ///     ["en", "ar", "ar", "fr"]: a deployment could add cultures but never
    ///     remove one.
    ///     <para>
    ///         So MinLength carries the requirement: a deployment that clears or
    ///         misspells this section is misconfigured, not defaulted, and refuses
    ///         to start - the same treatment as DefaultCulture above.
    ///     </para>
    /// </summary>
    [MinLength(1, ErrorMessage = "At least one supported culture must be configured.")]
    public string[] SupportedCultures { get; set; } = [];
}
