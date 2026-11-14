using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NatsSyncLab.BackendA.Module.Products.Infrastructure.Constants;
using NatsSyncLab.BackendA.Module.Products.Infrastructure.Messaging;
using NatsSyncLab.BackendA.Module.Products.Infrastructure.Persistence;
using NatsSyncLab.BackendA.SharedKernel;
using NatsSyncLab.SharedKernel;

namespace NatsSyncLab.BackendA.Module.Products.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddProductsModuleInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isProduction)
    {
        services.AddDatabaseContext<ProductsDbContext>(
            configuration,
            isProduction,
            DbConsts.MigrationHistoryTable,
            DbConsts.Schema);

        services.AddHostedService<OutboxRelayService>();
        services.AddEndpoints(AssemblyReference.Assembly);

        return services;
    }
}
