using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NatsSyncLab.SharedKernel;

namespace NatsSyncLab.BackendB.Module.Customers.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCustomersModuleInfrastructure(
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
