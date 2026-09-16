using BuildingBlocks.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Persistence;
using Persistence.Interceptors;
using Bookings.Contracts;
using Transactions.Contracts;
namespace Transactions;

public static class TransactionsServicesRegistration
{
    public static IServiceCollection ConfigureTransactionsServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment? environment = null)
    {

        services.AddSingleton<IModuleModel, TransactionsModel>();
        services.AddScoped<TransactionsDb>();

        services.AddScoped<TransactionReversal>();
        services.AddScoped<IPaymentReversal>(sp => sp.GetRequiredService<TransactionReversal>());

        return services;
    }
}
