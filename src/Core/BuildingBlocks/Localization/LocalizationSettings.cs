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
    public string DefaultCulture { get; init; } = "en";
    public string[] SupportedCultures { get; init; } = ["en", "ar"];
}
