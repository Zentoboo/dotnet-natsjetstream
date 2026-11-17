using System.Text.Json;
using System.Text.Json.Serialization;

namespace NatsSyncLab.SharedKernel.Contracts;

public sealed record DeadLetterEnvelope(
    [property: JsonPropertyName("eventId")] Guid? EventId,
    [property: JsonPropertyName("originalSubject")] string OriginalSubject,
    [property: JsonPropertyName("originalStream")] string? OriginalStream,
    [property: JsonPropertyName("originalStreamSequence")] long? OriginalStreamSequence,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("deliveryCount")] long DeliveryCount,
    [property: JsonPropertyName("failedAt")] DateTimeOffset FailedAt,
    [property: JsonPropertyName("rawPayload")] string? RawPayload,
    [property: JsonPropertyName("error")] string? Error);

public static class DeadLetterEnvelopeSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(DeadLetterEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, Options);

    public static DeadLetterEnvelope Deserialize(ReadOnlySpan<byte> data) =>
        JsonSerializer.Deserialize<DeadLetterEnvelope>(data, Options)
        ?? throw new InvalidOperationException("Could not deserialize dead-letter envelope.");
}
