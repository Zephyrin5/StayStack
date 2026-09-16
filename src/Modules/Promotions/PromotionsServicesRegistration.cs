using BuildingBlocks.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Persistence;
using Persistence.Interceptors;
using Promotions.Contracts;
namespace Promotions;

public static class PromotionsServicesRegistration
{
    public static IServiceCollection ConfigurePromotionsServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment? environment = null)
    {

        services.AddSingleton<IModuleModel, PromotionsModel>();
        services.AddScoped<PromotionsDb>();

        services.AddScoped<IPromotionRedemption, PromotionRedemption>();

        return services;
    }
}
