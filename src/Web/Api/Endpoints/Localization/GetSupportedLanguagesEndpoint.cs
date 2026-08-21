using Api.Localization;
using FastEndpoints;
using Microsoft.Extensions.Options;
namespace Api.Endpoints.Localization;

public class GetSupportedLanguagesEndpoint(IOptions<RequestLocalizationOptions> localizationOptions)
    : EndpointWithoutRequest<List<LanguageDto>>
{
    public override void Configure()
    {
        Get("");
        Group<LocalizationGroup>();
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "List supported languages";
            s.Description = "List the languages this deployment currently supports";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var supportedCodes = localizationOptions.Value.SupportedUICultures?
                                 .Select(c => c.TwoLetterISOLanguageName)
                             ?? [];

        var languages = LanguageCatalog.GetSupportedLanguages(supportedCodes.Distinct()).ToList();

        await Send.OkAsync(languages, ct);
    }
}
