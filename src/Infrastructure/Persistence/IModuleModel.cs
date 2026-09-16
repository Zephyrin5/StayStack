using Microsoft.EntityFrameworkCore;
namespace Persistence;

/// <summary>
///     One module's contribution to <see cref="AppDbContext"/>'s model. Each module registers one as a
///     singleton; the context calls them all and knows none of them.
/// </summary>
public interface IModuleModel
{
    void Configure(ModelBuilder builder);
}
