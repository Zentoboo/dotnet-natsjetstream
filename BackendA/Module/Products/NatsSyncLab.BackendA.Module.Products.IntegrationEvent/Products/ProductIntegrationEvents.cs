using System.Text.Json;
using NatsSyncLab.BackendA.Module.Products.Domain.Products;
using NatsSyncLab.SharedKernel.Contracts;
using NatsSyncLab.SharedKernel.IntegrationEvents;

namespace NatsSyncLab.BackendA.Module.Products.IntegrationEvent.Products;

public sealed record ProductIntegrationEvent(string Subject, EventEnvelope Envelope);

public static class ProductIntegrationEvents
{
    public static ProductIntegrationEvent Upserted(Product product, DateTimeOffset occurredAt) =>
        Create(product, EventOps.Upsert, occurredAt);

    public static ProductIntegrationEvent Deleted(Product product, DateTimeOffset occurredAt) =>
        Create(product, EventOps.Delete, occurredAt);

    private static ProductIntegrationEvent Create(Product product, string op, DateTimeOffset occurredAt)
    {
        var payload = new ProductSyncPayload(
            product.Id.Value,
            product.Name,
            product.Price,
            product.Version,
            product.IsDeleted,
            product.UpdatedAt);

        var envelope = new EventEnvelope(
            Guid.CreateVersion7(),
            EntityTypes.Product,
            product.Id.Value,
            product.Version,
            op,
            occurredAt,
            JsonSerializer.SerializeToElement(payload, EventEnvelopeSerializer.Options));

        envelope.EnsureValid();

        return new ProductIntegrationEvent(
            Subjects.ForOp(Sources.A, EntityTypes.Product, op),
            envelope);
    }
}
