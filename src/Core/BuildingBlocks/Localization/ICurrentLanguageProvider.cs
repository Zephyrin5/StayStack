namespace BuildingBlocks.Localization;

/// <summary>
///     Resolves the language for the current request. RequestLocalizationMiddleware
///     already sets CultureInfo.CurrentUICulture per-request (it flows via
///     AsyncLocal, so it's ambient through the whole request pipeline) -
///     this interface exists purely so handlers depend on an injectable
///     abstraction instead of a static BCL property, keeping them testable
///     the same way TimeProvider keeps hold-expiry logic testable instead
///     of calling DateTime.UtcNow directly.
/// </summary>
public interface ICurrentLanguageProvider
{
    string LanguageCode { get; }
}
