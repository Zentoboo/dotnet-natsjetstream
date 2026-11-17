using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Persistence;
using NatsSyncLab.SharedKernel.Chaos;
using NatsSyncLab.SharedKernel.Contracts;
using NatsSyncLab.SharedKernel.Diagnostics;
using NatsSyncLab.SharedKernel.IntegrationEvents;
using NatsSyncLab.SharedKernel.Messaging;

namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Messaging;

internal sealed class ProductsConsumerService(
    IServiceScopeFactory scopeFactory,
    INatsJSContext jetStream,
    IOptions<NatsOptions> natsOptions,
    TimeProvider timeProvider,
    SyncMetrics metrics,
    ILogger<ProductsConsumerService> logger) : BackgroundService
{
    private const string ConsumerName = "b-products-replica";
    private const string ProductSubject = "a.events.product.>";
    private const long MaxDeliveryAttempts = 5;
    private const long DeadLetterDeliveryAttempt = MaxDeliveryAttempts - 1;
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MessageErrorDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

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
                metrics.ConsumerErrors.Add(1);
                logger.LogError(exception, "Products replica consumer failed; retrying shortly.");
                await Task.Delay(FailureDelay, stoppingToken);
            }
        }
    }

    private async Task<INatsJSConsumer> CreateConsumerAsync(CancellationToken cancellationToken)
    {
        var config = new ConsumerConfig(ConsumerName)
        {
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            AckWait = TimeSpan.FromSeconds(30),
            MaxDeliver = MaxDeliveryAttempts,
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
            if (message.Metadata is { } metadata)
            {
                metrics.SetLastDeliveryCount((long)metadata.NumDelivered);
            }

            if (message.Error is not null)
            {
                metrics.ConsumerErrors.Add(1);
                logger.LogWarning(message.Error, "Received NATS message with an error.");
                await NakAsync(message, MessageErrorDelay, cancellationToken);
                continue;
            }

            var started = Stopwatch.GetTimestamp();

            try
            {
                var outcome = await HandleAsync(message, cancellationToken);

                Chaos.KillPoint("consumer-after-commit-before-ack");

                await message.AckAsync(cancellationToken: cancellationToken);

                if (outcome == HandleOutcome.Duplicate)
                {
                    metrics.ConsumerDuplicates.Add(1);
                }
                else
                {
                    metrics.ConsumerProcessed.Add(1);

                    if (outcome == HandleOutcome.Stale)
                    {
                        metrics.ConsumerStaleVersions.Add(1);
                    }
                }
            }
            catch (PermanentEventException exception)
            {
                metrics.ConsumerErrors.Add(1);
                await DeadLetterOrRetryAsync(message, exception.Reason, exception, cancellationToken);
            }
            catch (Exception exception)
            {
                metrics.ConsumerErrors.Add(1);
                var deliveryCount = GetDeliveryCount(message);

                if (deliveryCount >= DeadLetterDeliveryAttempt)
                {
                    await DeadLetterOrRetryAsync(message, "max-deliveries", exception, cancellationToken);
                    continue;
                }

                logger.LogError(exception, "Failed to apply product event; message will be retried.");
                await NakAsync(message, GetRetryDelay(message), cancellationToken);
            }
            finally
            {
                metrics.ConsumerProcessingDurationMs.Record(
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    private async Task<HandleOutcome> HandleAsync(INatsJSMsg<byte[]> message, CancellationToken cancellationToken)
    {
        EventEnvelope envelope;

        try
        {
            envelope = EventEnvelopeSerializer.Deserialize(message.Data);
            envelope.EnsureValid();
        }
        catch (Exception exception)
        {
            throw new PermanentEventException(
                "invalid-envelope",
                "Event envelope is invalid.",
                exception);
        }

        if (envelope.EntityType != EntityTypes.Product)
        {
            throw new PermanentEventException(
                "unsupported-entity",
                $"Unsupported entity type '{envelope.EntityType}'.");
        }

        var expectedSubject = Subjects.ForOp(Sources.A, EntityTypes.Product, envelope.Op);
        if (!string.Equals(message.Subject, expectedSubject, StringComparison.Ordinal))
        {
            throw new PermanentEventException(
                "subject-mismatch",
                $"Event subject '{message.Subject}' does not match operation '{envelope.Op}'.");
        }

        if (envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new PermanentEventException(
                "missing-payload",
                "Product event payload is missing.");
        }

        ProductSyncPayload payload;

        try
        {
            payload = envelope.Payload.Deserialize<ProductSyncPayload>(EventEnvelopeSerializer.Options)
                ?? throw new JsonException("Product event payload is missing.");
        }
        catch (JsonException exception)
        {
            throw new PermanentEventException(
                "invalid-payload",
                "Product event payload is invalid.",
                exception);
        }

        if (payload.Id != envelope.EntityId || payload.Version != envelope.Version)
        {
            throw new PermanentEventException(
                "payload-mismatch",
                "Product event payload does not match its envelope.");
        }

        if (envelope.Op == EventOps.Delete && !payload.IsDeleted)
        {
            throw new PermanentEventException(
                "delete-state-mismatch",
                "Delete event payload is not marked deleted.");
        }

        if (envelope.Op == EventOps.Upsert && payload.IsDeleted)
        {
            throw new PermanentEventException(
                "upsert-state-mismatch",
                "Upsert event payload is marked deleted.");
        }

        var now = timeProvider.GetUtcNow();
        metrics.SetInboxLagSeconds(Math.Max(0, (now - envelope.OccurredAt).TotalSeconds));
        var streamSequence = message.Metadata is { } metadata
            ? (long)metadata.Sequence.Stream
            : (long?)null;
        var deliveryCount = GetDeliveryCount(message);
        var outcome = HandleOutcome.Applied;

        await using var scope = scopeFactory.CreateAsyncScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProductsReplicaDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var executionStrategy = context.Database.CreateExecutionStrategy();

        await executionStrategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            outcome = HandleOutcome.Applied;

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var receivedAt = timeProvider.GetUtcNow();

            var inserted = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO products_replica.inbox (event_id, stream_sequence, received_at)
                 VALUES ({envelope.EventId}, {streamSequence}, {receivedAt})
                 ON CONFLICT (event_id) DO NOTHING
                 """,
                cancellationToken);

            Chaos.KillPoint("consumer-after-inbox-insert");

            if (inserted == 0)
            {
                outcome = HandleOutcome.Duplicate;
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation(
                    "Skipped duplicate event {EventId} at stream sequence {StreamSequence}, delivery {DeliveryCount}.",
                    envelope.EventId,
                    streamSequence,
                    deliveryCount);
                return;
            }

            var updated = await context.Database.ExecuteSqlInterpolatedAsync(
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

            Chaos.KillPoint("consumer-after-replica-upsert");

            await transaction.CommitAsync(cancellationToken);

            if (updated == 0)
            {
                outcome = HandleOutcome.Stale;
                logger.LogInformation(
                    "Skipped stale product event {EventId} for entity {EntityId} at version {Version}, stream sequence {StreamSequence}, delivery {DeliveryCount}.",
                    envelope.EventId,
                    envelope.EntityId,
                    envelope.Version,
                    streamSequence,
                    deliveryCount);
                return;
            }

            logger.LogInformation(
                "Applied product event {EventId} for entity {EntityId} at version {Version}, stream sequence {StreamSequence}, delivery {DeliveryCount}.",
                envelope.EventId,
                envelope.EntityId,
                envelope.Version,
                streamSequence,
                deliveryCount);
        });

        return outcome;
    }

    private async Task DeadLetterOrRetryAsync(
        INatsJSMsg<byte[]> message,
        string reason,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await DeadLetterAndTerminateAsync(message, reason, exception, cancellationToken);
        }
        catch (Exception deadLetterException)
        {
            logger.LogError(
                deadLetterException,
                "Failed to dead-letter event from {Subject} with reason {Reason}; message will be retried.",
                message.Subject,
                reason);

            await NakAsync(message, GetRetryDelay(message), cancellationToken);
        }
    }

    private async Task DeadLetterAndTerminateAsync(
        INatsJSMsg<byte[]> message,
        string reason,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var options = natsOptions.Value;
        var rawData = message.Data;
        var eventId = rawData is null ? null : TryReadEventId(rawData);
        var metadata = message.Metadata;
        var streamSequence = metadata is { } metadataValue
            ? (long)metadataValue.Sequence.Stream
            : (long?)null;
        var deliveryCount = GetDeliveryCount(message);
        var deadLetter = new DeadLetterEnvelope(
            eventId,
            message.Subject,
            metadata?.Stream,
            streamSequence,
            reason,
            deliveryCount,
            timeProvider.GetUtcNow(),
            rawData is null ? null : Encoding.UTF8.GetString(rawData),
            exception.ToString());

        var ack = await jetStream.PublishAsync(
            $"{options.DeadLetterSubjectPrefix}.product.failed",
            DeadLetterEnvelopeSerializer.Serialize(deadLetter),
            opts: new NatsJSPubOpts { MsgId = BuildDeadLetterMessageId(message, eventId) },
            cancellationToken: cancellationToken);

        if (ack.Error is not null)
        {
            throw new InvalidOperationException(
                $"JetStream rejected dead letter for event {eventId?.ToString() ?? "unknown"}: {ack.Error.Description}");
        }

        metrics.ConsumerDeadLettered.Add(1);

        logger.LogWarning(
            "Dead-lettered event {EventId} from {Subject} with reason {Reason}, stream sequence {StreamSequence}, delivery {DeliveryCount}.",
            eventId,
            message.Subject,
            reason,
            streamSequence,
            deliveryCount);

        await message.AckTerminateAsync(
            new AckOpts { TerminateReason = reason },
            cancellationToken);
        metrics.ConsumerTerminated.Add(1);
    }

    private async Task NakAsync(
        INatsJSMsg<byte[]> message,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        metrics.ConsumerNak.Add(1);
        await message.NakAsync(new AckOpts { NakDelay = delay }, cancellationToken);
    }

    private static Guid? TryReadEventId(ReadOnlySpan<byte> data)
    {
        try
        {
            var eventId = EventEnvelopeSerializer.Deserialize(data).EventId;
            return eventId == Guid.Empty ? null : eventId;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static TimeSpan GetRetryDelay(INatsJSMsg<byte[]> message)
    {
        var attempt = (int)Math.Clamp(GetDeliveryCount(message), 1, RetryDelays.Length);
        return RetryDelays[attempt - 1];
    }

    private static long GetDeliveryCount(INatsJSMsg<byte[]> message) =>
        message.Metadata is { } metadata ? (long)metadata.NumDelivered : 0;

    private static string BuildDeadLetterMessageId(INatsJSMsg<byte[]> message, Guid? eventId) =>
        message.Metadata is { } metadata
            ? $"{metadata.Stream}.{metadata.Sequence.Stream}"
            : eventId?.ToString() ?? Guid.NewGuid().ToString();

    private enum HandleOutcome
    {
        Applied,
        Duplicate,
        Stale,
    }
}
