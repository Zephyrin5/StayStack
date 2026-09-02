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
    public string DefaultCulture { get; set; } = "en";
    /// <summary>
    ///     No default value, deliberately. The binder does not replace a
    ///     collection that already has elements, it appends to it - so an
    ///     initializer of ["en", "ar"] plus a configured ["ar", "fr"] produced
    ///     ["en", "ar", "ar", "fr"], meaning a deployment could add supported
    ///     cultures but never remove one, and would silently get duplicates
    ///     trying. Empty here means "not configured", and the one consumer
    ///     supplies the fallback.
    /// </summary>
    public string[] SupportedCultures { get; set; } = [];
}
