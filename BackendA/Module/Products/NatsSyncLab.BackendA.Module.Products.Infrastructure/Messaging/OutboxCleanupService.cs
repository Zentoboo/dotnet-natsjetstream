using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NatsSyncLab.BackendA.Module.Products.Infrastructure.Persistence;
using NatsSyncLab.SharedKernel.Diagnostics;

namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Messaging;

internal sealed class OutboxCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxRetentionOptions> options,
    TimeProvider timeProvider,
    SyncMetrics metrics,
    ILogger<OutboxCleanupService> logger) : BackgroundService
{
    // Lab simplification: cleanup runs once at startup.
    // In production, use a periodic hosted service, TickerQ, Quartz, or an external scheduler.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CleanupAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Outbox cleanup failed. Old published rows will be retried on the next startup.");
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var retention = options.Value;
        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromDays(retention.RetentionDays);
        var totalDeleted = 0;

        await using var scope = scopeFactory.CreateAsyncScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProductsDbContext>>();

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var deleted = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 DELETE FROM products.outbox
                 WHERE id IN (
                     SELECT id
                     FROM products.outbox
                     WHERE published_at IS NOT NULL
                       AND published_at < {cutoff}
                     ORDER BY id
                     LIMIT {retention.BatchSize}
                 )
                 """,
                cancellationToken);

            totalDeleted += deleted;

            if (deleted < retention.BatchSize)
            {
                break;
            }
        }

        if (totalDeleted > 0)
        {
            metrics.OutboxCleanupDeleted.Add(totalDeleted);
            logger.LogInformation(
                "Deleted {Count} published outbox row(s) older than {Cutoff}.",
                totalDeleted,
                cutoff);
        }
    }
}
