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

        services.AddOptions<InboxRetentionOptions>()
            .Bind(configuration.GetSection(InboxRetentionOptions.SectionName))
            .Validate(
                options => options.RetentionDays > 0,
                "Inbox retention days must be greater than zero.")
            .Validate(
                options => options.BatchSize > 0,
                "Inbox cleanup batch size must be greater than zero.")
            .ValidateOnStart();

        services.AddHostedService<ProductsConsumerService>();
        services.AddHostedService<InboxCleanupService>();
        services.AddEndpoints(AssemblyReference.Assembly);

        return services;
    }
}
