using NatsSyncLab.BackendA.Module.Products.Domain.Products;

namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Endpoints;

internal sealed record ProductResponse(
    Guid Id,
    string Name,
    decimal Price,
    long Version,
    bool IsDeleted,
    DateTimeOffset UpdatedAt)
{
    public static ProductResponse From(Product product) =>
        new(
            product.Id.Value,
            product.Name,
            product.Price,
            product.Version,
            product.IsDeleted,
            product.UpdatedAt);
}
