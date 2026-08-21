using FastEndpoints;
namespace Api.Endpoints.Auth;

public sealed class AuthGroup : Group
{
    public AuthGroup()
    {
        Configure("api/auth", ep => { ep.Description(b => b.WithTags("Auth")); });
    }
}
