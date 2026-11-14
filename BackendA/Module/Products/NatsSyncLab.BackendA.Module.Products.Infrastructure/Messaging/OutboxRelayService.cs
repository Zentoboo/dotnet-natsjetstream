using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NatsSyncLab.BackendA.Module.Products.Infrastructure.Persistence;

namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Messaging;

internal sealed class OutboxRelayService(
    IServiceScopeFactory scopeFactory,
    INatsJSContext jetStream,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var publishedCount = await PublishBatchAsync(stoppingToken);

                if (publishedCount == 0)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox relay failed; retrying shortly.");
                await Task.Delay(FailureDelay, stoppingToken);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProductsDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var executionStrategy = context.Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var messages = await context.Outbox
                .FromSqlInterpolated(
                    $"SELECT * FROM products.outbox WHERE published_at IS NULL ORDER BY id LIMIT {BatchSize} FOR UPDATE SKIP LOCKED")
                .ToListAsync(cancellationToken);

            if (messages.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return 0;
            }

            var publishedAt = TimeProvider.System.GetUtcNow();

            foreach (var message in messages)
            {
                var ack = await jetStream.PublishAsync(
                    message.Subject,
                    Encoding.UTF8.GetBytes(message.Payload),
                    opts: new NatsJSPubOpts { MsgId = message.EventId.ToString() },
                    cancellationToken: cancellationToken);

                if (ack.Error is not null)
                {
                    throw new InvalidOperationException(
                        $"JetStream rejected outbox event {message.EventId}: {ack.Error.Description}");
                }

                message.MarkPublished(publishedAt);
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("Published {Count} outbox event(s).", messages.Count);
            return messages.Count;
        });
    }
}
