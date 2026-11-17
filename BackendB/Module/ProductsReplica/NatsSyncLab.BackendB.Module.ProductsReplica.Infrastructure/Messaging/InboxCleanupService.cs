using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Persistence;

namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Messaging;

internal sealed class InboxCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<InboxRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<InboxCleanupService> logger) : BackgroundService
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
                "Inbox cleanup failed. Old rows will be retried on the next startup.");
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var retention = options.Value;
        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromDays(retention.RetentionDays);
        var totalDeleted = 0;

        await using var scope = scopeFactory.CreateAsyncScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProductsReplicaDbContext>>();

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var deleted = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 DELETE FROM products_replica.inbox
                 WHERE event_id IN (
                     SELECT event_id
                     FROM products_replica.inbox
                     WHERE received_at < {cutoff}
                     ORDER BY received_at
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
            logger.LogInformation(
                "Deleted {Count} inbox row(s) older than {Cutoff}.",
                totalDeleted,
                cutoff);
        }
    }
}
