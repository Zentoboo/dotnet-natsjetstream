using System.Text;
using NatsSyncLab.BackendA.Module.Products.Domain.Products;
using NatsSyncLab.BackendA.Module.Products.IntegrationEvent.Products;
using NatsSyncLab.SharedKernel.Contracts;
using NatsSyncLab.SharedKernel.Persistence;

namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Messaging;

internal static class ProductOutbox
{
    public static OutboxMessage Upsert(Product product, DateTimeOffset occurredAt) =>
        Create(ProductIntegrationEvents.Upserted(product, occurredAt));

    public static OutboxMessage Delete(Product product, DateTimeOffset occurredAt) =>
        Create(ProductIntegrationEvents.Deleted(product, occurredAt));

    private static OutboxMessage Create(ProductIntegrationEvent integrationEvent)
    {
        var payload = Encoding.UTF8.GetString(EventEnvelopeSerializer.Serialize(integrationEvent.Envelope));

        return OutboxMessage.Create(
            integrationEvent.Envelope.EventId,
            integrationEvent.Subject,
            payload);
    }
}
