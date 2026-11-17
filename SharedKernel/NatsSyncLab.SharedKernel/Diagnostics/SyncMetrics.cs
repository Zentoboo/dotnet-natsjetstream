using System.Diagnostics.Metrics;

namespace NatsSyncLab.SharedKernel.Diagnostics;

public sealed class SyncMetrics : IDisposable
{
    public const string MeterName = "NatsSyncLab";

    private readonly Meter _meter;
    private long _outboxBacklog;
    private double _inboxLagSeconds;
    private long _lastDeliveryCount;

    public SyncMetrics()
    {
        _meter = new Meter(MeterName);

        OutboxPublished = _meter.CreateCounter<long>("outbox.published");
        OutboxPublishFailures = _meter.CreateCounter<long>("outbox.publish_failures");
        OutboxCleanupDeleted = _meter.CreateCounter<long>("outbox.cleanup_deleted");
        ConsumerProcessed = _meter.CreateCounter<long>("consumer.processed");
        ConsumerDuplicates = _meter.CreateCounter<long>("consumer.duplicates");
        ConsumerStaleVersions = _meter.CreateCounter<long>("consumer.stale_versions");
        ConsumerNak = _meter.CreateCounter<long>("consumer.nak");
        ConsumerDeadLettered = _meter.CreateCounter<long>("consumer.dead_lettered");
        ConsumerTerminated = _meter.CreateCounter<long>("consumer.terminated");
        ConsumerErrors = _meter.CreateCounter<long>("consumer.errors");

        OutboxBatchDurationMs = _meter.CreateHistogram<double>("outbox.batch.duration_ms", unit: "ms");
        ConsumerProcessingDurationMs = _meter.CreateHistogram<double>("consumer.processing.duration_ms", unit: "ms");

        _meter.CreateObservableGauge(
            "outbox.backlog",
            () => new Measurement<long>(Volatile.Read(ref _outboxBacklog)));

        _meter.CreateObservableGauge(
            "inbox.lag_seconds",
            () => new Measurement<double>(Volatile.Read(ref _inboxLagSeconds)));

        _meter.CreateObservableGauge(
            "consumer.last_delivery_count",
            () => new Measurement<long>(Volatile.Read(ref _lastDeliveryCount)));
    }

    public Counter<long> OutboxPublished { get; }

    public Counter<long> OutboxPublishFailures { get; }

    public Counter<long> OutboxCleanupDeleted { get; }

    public Counter<long> ConsumerProcessed { get; }

    public Counter<long> ConsumerDuplicates { get; }

    public Counter<long> ConsumerStaleVersions { get; }

    public Counter<long> ConsumerNak { get; }

    public Counter<long> ConsumerDeadLettered { get; }

    public Counter<long> ConsumerTerminated { get; }

    public Counter<long> ConsumerErrors { get; }

    public Histogram<double> OutboxBatchDurationMs { get; }

    public Histogram<double> ConsumerProcessingDurationMs { get; }

    public void SetOutboxBacklog(long value) => Volatile.Write(ref _outboxBacklog, value);

    public void SetInboxLagSeconds(double value) => Volatile.Write(ref _inboxLagSeconds, value);

    public void SetLastDeliveryCount(long value) => Volatile.Write(ref _lastDeliveryCount, value);

    public void Dispose() => _meter.Dispose();
}
