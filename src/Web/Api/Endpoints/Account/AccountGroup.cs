using FastEndpoints;
namespace Api.Endpoints.Account;

public sealed class AccountGroup : Group
{
    public AccountGroup()
    {
        Configure("api/account", ep => { ep.Description(b => b.WithTags("Account")); });
    }
}
