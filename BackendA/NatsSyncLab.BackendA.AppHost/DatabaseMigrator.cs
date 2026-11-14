using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NatsSyncLab.BackendA.Module.Products.Infrastructure.Persistence;

namespace NatsSyncLab.BackendA.AppHost;

internal static class DatabaseMigrator
{
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProductsDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.Database.MigrateAsync(cancellationToken);
    }
}
