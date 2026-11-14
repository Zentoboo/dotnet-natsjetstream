namespace NatsSyncLab.SharedKernel.Persistence;

public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    public long Id { get; init; }

    public Guid EventId { get; init; }

    public string Subject { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? PublishedAt { get; private set; }

    public static OutboxMessage Create(Guid eventId, string subject, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new OutboxMessage
        {
            EventId = eventId,
            Subject = subject,
            Payload = payload,
        };
    }

    public void MarkPublished(DateTimeOffset publishedAt) => PublishedAt = publishedAt;
}
