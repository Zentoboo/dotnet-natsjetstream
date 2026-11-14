using System.Text.Json.Serialization;

namespace NatsSyncLab.SharedKernel.IntegrationEvents;

public sealed record ProductSyncPayload(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("isDeleted")] bool IsDeleted,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);
