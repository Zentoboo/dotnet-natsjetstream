using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Constants;
using NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Messaging;
using NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Persistence;
using NatsSyncLab.BackendB.SharedKernel;
using NatsSyncLab.SharedKernel;

namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddProductsReplicaModuleInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isProduction)
    {
        services.AddDatabaseContext<ProductsReplicaDbContext>(
            configuration,
            isProduction,
            DbConsts.MigrationHistoryTable,
            DbConsts.Schema);

        services.AddHostedService<ProductsConsumerService>();
        services.AddEndpoints(AssemblyReference.Assembly);

        return services;
    }
}
