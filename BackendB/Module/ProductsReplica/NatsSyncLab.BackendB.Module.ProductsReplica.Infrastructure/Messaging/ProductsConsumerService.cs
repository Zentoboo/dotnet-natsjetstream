using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Persistence;
using NatsSyncLab.SharedKernel.Contracts;
using NatsSyncLab.SharedKernel.IntegrationEvents;
using NatsSyncLab.SharedKernel.Messaging;

namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Messaging;

internal sealed class ProductsConsumerService(
    IServiceScopeFactory scopeFactory,
    INatsJSContext jetStream,
    IOptions<NatsOptions> natsOptions,
    TimeProvider timeProvider,
    ILogger<ProductsConsumerService> logger) : BackgroundService
{
    private const string ConsumerName = "b-products-replica";
    private const string ProductSubject = "a.events.product.>";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await CreateConsumerAsync(stoppingToken);
                await ConsumeAsync(consumer, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Products replica consumer failed; retrying shortly.");
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }

    private async Task<INatsJSConsumer> CreateConsumerAsync(CancellationToken cancellationToken)
    {
        var config = new ConsumerConfig(ConsumerName)
        {
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            AckWait = TimeSpan.FromSeconds(30),
            MaxDeliver = 5,
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            FilterSubject = ProductSubject,
        };

        return await jetStream.CreateOrUpdateConsumerAsync(
            natsOptions.Value.StreamA,
            config,
            cancellationToken);
    }

    private async Task ConsumeAsync(INatsJSConsumer consumer, CancellationToken cancellationToken)
    {
        var fetchOptions = new NatsJSFetchOpts
        {
            MaxMsgs = 10,
            Expires = TimeSpan.FromSeconds(5),
        };

        await foreach (var message in consumer.FetchAsync<byte[]>(fetchOptions, cancellationToken: cancellationToken))
        {
            if (message.Error is not null)
            {
                logger.LogWarning(message.Error, "Received NATS message with an error.");
                await message.NakAsync(new AckOpts { NakDelay = RetryDelay }, cancellationToken);
                continue;
            }

            try
            {
                if (!await HandleAsync(message, cancellationToken))
                {
                    continue;
                }

                await message.AckAsync(cancellationToken: cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to apply product event; message will be retried.");
                await message.NakAsync(new AckOpts { NakDelay = RetryDelay }, cancellationToken);
            }
        }
    }

    private async Task<bool> HandleAsync(INatsJSMsg<byte[]> message, CancellationToken cancellationToken)
    {
        EventEnvelope envelope;

        try
        {
            envelope = EventEnvelopeSerializer.Deserialize(message.Data);
            envelope.EnsureValid();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Discarding invalid event envelope.");
            await message.AckTerminateAsync(
                new AckOpts { TerminateReason = "invalid-envelope" },
                cancellationToken);
            return false;
        }

        if (envelope.EntityType != EntityTypes.Product)
        {
            logger.LogWarning(
                "Discarding unsupported entity type {EntityType} from {Subject}.",
                envelope.EntityType,
                message.Subject);
            await message.AckTerminateAsync(
                new AckOpts { TerminateReason = "unsupported-entity" },
                cancellationToken);
            return false;
        }

        var payload = envelope.Payload.Deserialize<ProductSyncPayload>(EventEnvelopeSerializer.Options)
            ?? throw new InvalidOperationException("Product event payload is missing.");

        if (payload.Id != envelope.EntityId || payload.Version != envelope.Version)
        {
            throw new InvalidOperationException("Product event payload does not match its envelope.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProductsReplicaDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var executionStrategy = context.Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var now = timeProvider.GetUtcNow();
            long? streamSequence = message.Metadata is { } metadata
                ? (long)metadata.Sequence.Stream
                : null;

            var inserted = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO products_replica.inbox (event_id, stream_sequence, received_at)
                 VALUES ({envelope.EventId}, {streamSequence}, {now})
                 ON CONFLICT (event_id) DO NOTHING
                 """,
                cancellationToken);

            if (inserted == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation("Skipped duplicate event {EventId}.", envelope.EventId);
                return true;
            }

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO products_replica.products_replica
                     (id, name, price, version, is_deleted, updated_at, synced_at)
                 VALUES ({payload.Id}, {payload.Name}, {payload.Price}, {payload.Version}, {payload.IsDeleted}, {payload.UpdatedAt}, {now})
                 ON CONFLICT (id) DO UPDATE
                 SET name = EXCLUDED.name,
                     price = EXCLUDED.price,
                     version = EXCLUDED.version,
                     is_deleted = EXCLUDED.is_deleted,
                     updated_at = EXCLUDED.updated_at,
                     synced_at = EXCLUDED.synced_at
                 WHERE products_replica.products_replica.version < EXCLUDED.version
                 """,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Applied product event {EventId} for entity {EntityId} at version {Version}.",
                envelope.EventId,
                envelope.EntityId,
                envelope.Version);

            return true;
        });
    }
}
