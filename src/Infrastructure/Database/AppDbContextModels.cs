using Bookings;
using Catalog;
using Hosts;
using Identity;
using Persistence;
using Promotions;
using Reviews;
using Transactions;
namespace Database;

/// <summary>
///     Every module's model contribution, for callers outside the DI container - the design-time
///     factory, and the migrations built from it.
/// </summary>
public static class AppDbContextModels
{
    public static IReadOnlyList<IModuleModel> All =>
    [
        new IdentityModel(),
        new HostsModel(),
        new CatalogModel(),
        new PromotionsModel(),
        new BookingsModel(),
        new TransactionsModel(),
        new ReviewsModel()
    ];
}
