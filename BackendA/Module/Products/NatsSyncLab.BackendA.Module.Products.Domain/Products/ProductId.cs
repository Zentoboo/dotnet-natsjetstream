namespace NatsSyncLab.BackendA.Module.Products.Domain.Products;

public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());

    public static ProductId Of(Guid value) => new(value);
}
