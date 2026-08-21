using System.Globalization;
namespace Api.Localization;

public static class LanguageCatalog
{
    // No Language table backs this - Code comes from appsettings'
    // Localization:SupportedCultures, NativeName/IsRtl come straight from
    // CultureInfo's ICU data, which already knows this correctly for every
    // culture .NET supports. There was nothing left for a database table
    // to actually own once both of those were accounted for.
    public static IReadOnlyList<LanguageDto> GetSupportedLanguages(IEnumerable<string> supportedCultureCodes)
    {
        return
        [
            .. supportedCultureCodes
                .Select(code =>
                {
                    CultureInfo culture = CultureInfo.GetCultureInfo(code);
                    return new LanguageDto
                    {
                        Code = code,
                        NativeName = culture.NativeName,
                        IsRtl = culture.TextInfo.IsRightToLeft
                    };
                })
        ];
    }
}
