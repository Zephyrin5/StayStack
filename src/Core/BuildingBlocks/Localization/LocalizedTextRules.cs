using FluentValidation;
namespace BuildingBlocks.Localization;

/// <summary>
///     The one statement of what a localized dictionary may contain on the way in.
///     <para>
///         <c>LocalizedText.Create</c> already refuses a payload with no value for the default
///         culture - by throwing <see cref="ArgumentException" />, which no handler catches and
///         <c>GlobalExceptionHandler</c> deliberately has no arm for, so it left the request as a
///         500. What it never checked is the rest: a key nobody serves, or a value of any length at
///         all, both of which reached <c>jsonb</c> and stayed there.
///     </para>
///     <para>
///         Here rather than in SeedWork beside the type: the value object has no opinion about which
///         cultures a deployment serves, and taking one would mean SeedWork reading configuration.
///         This project is where the modules already share that answer.
///     </para>
/// </summary>
public static class LocalizedTextRules
{
    /// <summary>
    ///     What a name may run to, in characters. The column is <c>jsonb</c> and bounds nothing, so
    ///     this is the bound - shared by every localized name so one endpoint cannot quietly accept
    ///     what another refuses.
    /// </summary>
    public const int MaxNameLength = 200;

    /// <summary>A host's public display name, bounded the same way and for the same reason.</summary>
    public const int MaxDisplayNameLength = 200;

    /// <summary>
    ///     Every key is a culture this deployment serves, every value is present and within
    ///     <paramref name="maxLength" />, and the default culture is one of them.
    /// </summary>
    public static IRuleBuilderOptions<T, IDictionary<string, string>?> LocalizedText<T>(
        this IRuleBuilder<T, IDictionary<string, string>?> rule, LocalizationSettings settings, int maxLength) =>
        rule
            .Must(values => values is not null
                            && values.TryGetValue(settings.DefaultCulture, out string? required)
                            && !string.IsNullOrWhiteSpace(required))
            .WithMessage($"A non-empty '{settings.DefaultCulture}' value is required.")
            // Each check names what it refuses on its own, because "invalid" over a dictionary sends
            // the caller looking through every entry to find which one.
            .Must(values => values is null || values.Keys.All(settings.SupportedCultures.Contains))
            .WithMessage($"Every key must be one of the supported cultures: {{SupportedCultures}}."
                .Replace("{SupportedCultures}", string.Join(", ", settings.SupportedCultures), StringComparison.Ordinal))
            .Must(values => values is null || values.Values.All(value => !string.IsNullOrWhiteSpace(value)))
            .WithMessage("A localized value cannot be empty or whitespace.")
            .Must(values => values is null || values.Values.All(value => value.Length <= maxLength))
            .WithMessage($"A localized value cannot be longer than {maxLength} characters.");
}
