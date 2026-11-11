using System.Text.Json;
using System.Text.Json.Serialization;

namespace NatsSyncLab.BuildingBlocks.Contracts;

public sealed record EventEnvelope(
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("entityType")] string EntityType,
    [property: JsonPropertyName("entityId")] Guid EntityId,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("payload")] JsonElement Payload)
{
    public void EnsureValid()
    {
        if (EventId == Guid.Empty)
        {
            throw new InvalidOperationException("EventId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(EntityType))
        {
            throw new InvalidOperationException("EntityType must be set.");
        }

        if (EntityId == Guid.Empty)
        {
            throw new InvalidOperationException("EntityId must not be empty.");
        }

        if (Version <= 0)
        {
            throw new InvalidOperationException("Version must be greater than zero.");
        }

        if (!EventOps.IsKnown(Op))
        {
            throw new InvalidOperationException($"Unknown event operation '{Op}'.");
        }
    }
}

public static class EventEnvelopeSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(EventEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, Options);

    public static EventEnvelope Deserialize(ReadOnlySpan<byte> data) =>
        JsonSerializer.Deserialize<EventEnvelope>(data, Options)
        ?? throw new InvalidOperationException("Could not deserialize event envelope.");
}
