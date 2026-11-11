using System.Text.Json;
using NatsSyncLab.BuildingBlocks.Contracts;
using Xunit;

namespace NatsSyncLab.Tests;

public sealed class EventEnvelopeTests
{
    [Fact]
    public void Serialize_round_trips_all_fields()
    {
        using var document = JsonDocument.Parse("""{"name":"Widget","price":12.5}""");
        var eventId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
        var envelope = new EventEnvelope(
            eventId,
            EntityTypes.Product,
            entityId,
            7,
            EventOps.Upsert,
            occurredAt,
            document.RootElement.Clone());

        var bytes = EventEnvelopeSerializer.Serialize(envelope);
        var roundTripped = EventEnvelopeSerializer.Deserialize(bytes);

        Assert.Equal(eventId, roundTripped.EventId);
        Assert.Equal(EntityTypes.Product, roundTripped.EntityType);
        Assert.Equal(entityId, roundTripped.EntityId);
        Assert.Equal(7, roundTripped.Version);
        Assert.Equal(EventOps.Upsert, roundTripped.Op);
        Assert.Equal(occurredAt, roundTripped.OccurredAt);
        Assert.Equal("Widget", roundTripped.Payload.GetProperty("name").GetString());
        Assert.Equal(12.5m, roundTripped.Payload.GetProperty("price").GetDecimal());
    }

    [Fact]
    public void Serialize_uses_camel_case_property_names()
    {
        using var document = JsonDocument.Parse("{}");
        var envelope = new EventEnvelope(
            Guid.NewGuid(),
            EntityTypes.Customer,
            Guid.NewGuid(),
            1,
            EventOps.Delete,
            DateTimeOffset.UtcNow,
            document.RootElement.Clone());

        var json = System.Text.Encoding.UTF8.GetString(EventEnvelopeSerializer.Serialize(envelope));

        Assert.Contains("\"eventId\"", json);
        Assert.Contains("\"entityType\"", json);
        Assert.Contains("\"entityId\"", json);
        Assert.Contains("\"version\"", json);
        Assert.Contains("\"op\"", json);
        Assert.Contains("\"occurredAt\"", json);
        Assert.Contains("\"payload\"", json);
    }

    [Fact]
    public void EnsureValid_rejects_unknown_operation()
    {
        using var document = JsonDocument.Parse("{}");
        var envelope = new EventEnvelope(
            Guid.NewGuid(),
            EntityTypes.Product,
            Guid.NewGuid(),
            1,
            "exploded",
            DateTimeOffset.UtcNow,
            document.RootElement.Clone());

        Assert.Throws<InvalidOperationException>(envelope.EnsureValid);
    }
}
