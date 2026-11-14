namespace NatsSyncLab.SharedKernel.Persistence;

public sealed class InboxMessage
{
    private InboxMessage()
    {
    }

    public Guid EventId { get; init; }

    public long? StreamSequence { get; init; }

    public DateTimeOffset ReceivedAt { get; init; } = TimeProvider.System.GetUtcNow();

    public static InboxMessage Create(Guid eventId, long? streamSequence = null) =>
        new()
        {
            EventId = eventId,
            StreamSequence = streamSequence,
        };
}
