using BuildingBlocks.Identity;
using System.Security.Claims;
namespace Api.Security;

public class HttpContextCurrentUserProvider(IHttpContextAccessor httpContextAccessor) : ICurrentUserProvider
{
    public Guid? UserId
    {
        get
        {
            string? sub = httpContextAccessor.HttpContext?.User.FindFirst("sub")?.Value;
            return Guid.TryParse(sub, out Guid userId) ? userId : null;
        }
    }

    public Guid? HostId
    {
        get
        {
            string? hostId = httpContextAccessor.HttpContext?.User.FindFirst("host_id")?.Value;
            return Guid.TryParse(hostId, out Guid parsed) ? parsed : null;
        }
    }

    public IReadOnlyCollection<string> Roles =>
        httpContextAccessor.HttpContext?.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray()
        ?? [];
}
