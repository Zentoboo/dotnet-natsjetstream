using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NatsSyncLab.SharedKernel;

namespace NatsSyncLab.BackendA.SharedKernel;

public static class DependencyInjection
{
    public static IServiceCollection AddBackendASharedKernel(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        _ = configuration;
        return services;
    }

    public static IServiceCollection AddDatabaseContext<TDbContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isProduction,
        string migrationTableName,
        string schemaName)
        where TDbContext : DbContext
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

        services.AddPostgres<TDbContext>(
            connectionString,
            isProduction,
            migrationTableName,
            schemaName);

        services.AddPostgresFactory(connectionString);

        return services;
    }
}
