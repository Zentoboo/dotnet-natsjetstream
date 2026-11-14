using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Persistence;

namespace NatsSyncLab.BackendB.AppHost;

internal static class DatabaseMigrator
{
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProductsReplicaDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.Database.MigrateAsync(cancellationToken);
    }
}
