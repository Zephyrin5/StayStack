using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
namespace BuildingBlocks.Localization;

/// <summary>
///     Bound from the "Localization" section: the platform-wide answer to "which language is
///     required" that LocalizedText.Create needs from every caller, since SeedWork deliberately has no
///     opinion of its own.
/// </summary>
public class LocalizationSettings
{
    public const string SectionName = "Localization";

    // set, not init: the generated binder assigns properties directly, and silently skips init-only
    // ones - bound against init, both of these kept their defaults.
    [Required(AllowEmptyStrings = false)]
    public string DefaultCulture { get; set; } = "en";
    /// <summary>
    ///     No default: the binder appends to a collection that already has elements rather than
    ///     replacing it, so a deployment could add cultures but never remove one. MinLength carries the
    ///     requirement instead - a cleared or misspelled section refuses to start.
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
