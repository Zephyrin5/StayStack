using BuildingBlocks.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Persistence;
using Persistence.Interceptors;
namespace Reviews;

public static class ReviewsServicesRegistration
{
    public static IServiceCollection ConfigureReviewsServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment? environment = null)
    {

        services.AddSingleton<IModuleModel, ReviewsModel>();
        services.AddScoped<ReviewsDb>();

        return services;
    }
}
