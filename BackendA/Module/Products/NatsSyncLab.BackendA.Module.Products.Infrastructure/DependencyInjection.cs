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

        services.AddOptions<OutboxRetentionOptions>()
            .Bind(configuration.GetSection(OutboxRetentionOptions.SectionName))
            .Validate(
                options => options.RetentionDays > 0,
                "Outbox retention days must be greater than zero.")
            .Validate(
                options => options.BatchSize > 0,
                "Outbox cleanup batch size must be greater than zero.")
            .ValidateOnStart();

        services.AddHostedService<OutboxRelayService>();
        services.AddHostedService<OutboxCleanupService>();
        services.AddEndpoints(AssemblyReference.Assembly);

        return services;
    }
}
