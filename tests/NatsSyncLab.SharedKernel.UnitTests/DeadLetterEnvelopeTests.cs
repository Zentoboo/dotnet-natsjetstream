using System.Text;
using NatsSyncLab.SharedKernel.Contracts;
using Xunit;

namespace NatsSyncLab.SharedKernel.UnitTests;

public sealed class DeadLetterEnvelopeTests
{
    [Fact]
    public void Serialize_round_trips_all_fields()
    {
        var eventId = Guid.NewGuid();
        var failedAt = new DateTimeOffset(2026, 11, 17, 9, 30, 0, TimeSpan.Zero);
        var envelope = new DeadLetterEnvelope(
            eventId,
            "a.events.product.upserted",
            "A_EVENTS",
            42,
            "payload-mismatch",
            3,
            failedAt,
            """{"id":"8fb7a2f4-4c3b-4f3d-9a2e-1b6d7c8e9f01","name":"Widget"}""",
            "InvalidOperationException: Product event payload does not match its envelope.");

        var bytes = DeadLetterEnvelopeSerializer.Serialize(envelope);
        var roundTripped = DeadLetterEnvelopeSerializer.Deserialize(bytes);

        Assert.Equal(envelope, roundTripped);
    }

    [Fact]
    public void Serialize_round_trips_without_event_identity()
    {
        var failedAt = new DateTimeOffset(2026, 11, 17, 9, 30, 0, TimeSpan.Zero);
        var envelope = new DeadLetterEnvelope(
            null,
            "a.events.product.upserted",
            null,
            null,
            "invalid-envelope",
            1,
            failedAt,
            "not-json",
            "JsonException: The JSON value could not be converted.");

        var bytes = DeadLetterEnvelopeSerializer.Serialize(envelope);
        var roundTripped = DeadLetterEnvelopeSerializer.Deserialize(bytes);

        Assert.Equal(envelope, roundTripped);
    }

    [Fact]
    public void Serialize_uses_camel_case_property_names()
    {
        var envelope = new DeadLetterEnvelope(
            null,
            "a.events.product.upserted",
            null,
            null,
            "invalid-envelope",
            1,
            DateTimeOffset.UtcNow,
            null,
            null);

        var json = Encoding.UTF8.GetString(DeadLetterEnvelopeSerializer.Serialize(envelope));

        Assert.Contains("\"eventId\"", json);
        Assert.Contains("\"originalSubject\"", json);
        Assert.Contains("\"originalStream\"", json);
        Assert.Contains("\"originalStreamSequence\"", json);
        Assert.Contains("\"reason\"", json);
        Assert.Contains("\"deliveryCount\"", json);
        Assert.Contains("\"failedAt\"", json);
        Assert.Contains("\"rawPayload\"", json);
        Assert.Contains("\"error\"", json);
    }
}
