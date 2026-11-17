using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NatsSyncLab.SharedKernel.Abstractions;
using NatsSyncLab.SharedKernel.Diagnostics;
using NatsSyncLab.SharedKernel.Persistence;
using Npgsql;

namespace NatsSyncLab.SharedKernel;

public static class DependencyInjection
{
    public static IServiceCollection AddSharedKernel(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SyncMetrics>();
        return services;
    }

    public static IServiceCollection AddEndpoints(this IServiceCollection services, params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var endpointTypes = assembly
                .GetTypes()
                .Where(type => typeof(IEndpoint).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false });

            foreach (var endpointType in endpointTypes)
            {
                services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IEndpoint), endpointType));
            }
        }

        return services;
    }

    public static IEndpointRouteBuilder MapEndpoints(
        this IEndpointRouteBuilder app,
        IEndpointRouteBuilder group)
    {
        foreach (var endpoint in app.ServiceProvider.GetServices<IEndpoint>())
        {
            endpoint.Configure(group);
        }

        return group;
    }

    public static IServiceCollection AddPostgresFactory(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton<IDbConnectionFactory>(_ => new NpgsqlConnectionFactory(connectionString));
        return services;
    }

    public static IServiceCollection AddPostgres<T>(
        this IServiceCollection services,
        string connectionString,
        bool isProduction,
        string migrationTableName,
        string schemaName)
        where T : DbContext
    {
        _ = isProduction;

        services.TryAddSingleton(_ =>
            new NpgsqlDataSourceBuilder(connectionString).EnableDynamicJson().Build());

        services.AddDbContextFactory<T>((serviceProvider, options) =>
        {
            options.UseNpgsql(serviceProvider.GetRequiredService<NpgsqlDataSource>(), npgsql =>
            {
                npgsql
                    .MigrationsAssembly(typeof(T).Assembly.FullName)
                    .MigrationsHistoryTable(migrationTableName, schemaName)
                    .EnableRetryOnFailure(5, TimeSpan.FromSeconds(30), null);
            });
        }, ServiceLifetime.Scoped);

        services.AddScoped(serviceProvider =>
            serviceProvider.GetRequiredService<IDbContextFactory<T>>().CreateDbContext());

        return services;
    }
}
