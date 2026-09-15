using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
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
    // below.
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
    // MinLengthAttribute is flagged for trimming because its IsValid reflects
    // over a Count property on types that are not collections. It is validated
    // by the source-generated LocalizationSettingsAttributesValidator, which
    // emits its own reflection-free length check; the attribute's IsValid is
    // never called at runtime.
    [MinLength(1, ErrorMessage = "At least one supported culture must be configured.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Validated by the source-generated options validator, not MinLengthAttribute.IsValid.")]
    public string[] SupportedCultures { get; set; } = [];
}
