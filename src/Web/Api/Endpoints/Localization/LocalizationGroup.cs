using FastEndpoints;
namespace Api.Endpoints.Localization;

public sealed class LocalizationGroup : Group
{
    public LocalizationGroup()
    {
        Configure("api/languages", ep => { ep.Description(b => b.WithTags("Localization")); });
    }
}
