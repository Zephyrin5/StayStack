using BuildingBlocks.Localization;
using System.Globalization;
namespace Api.Localization;

public class CultureInfoLanguageProvider : ICurrentLanguageProvider
{
    public string LanguageCode => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
}
