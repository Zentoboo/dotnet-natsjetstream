using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NatsSyncLab.SharedKernel;

namespace NatsSyncLab.BackendA.Module.CustomersReplica.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCustomersReplicaModuleInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isProduction)
    {
        _ = configuration;
        _ = isProduction;

        services.AddEndpoints(AssemblyReference.Assembly);
        return services;
    }
}
