using FastEndpoints;
namespace Api.Endpoints.Users;

public sealed class UsersGroup : Group
{
    public UsersGroup()
    {
        Configure("api/users", ep => { ep.Description(b => b.WithTags("Users")); });
    }
}
