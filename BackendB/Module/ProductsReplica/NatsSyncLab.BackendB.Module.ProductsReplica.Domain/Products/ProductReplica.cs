namespace NatsSyncLab.BackendB.Module.ProductsReplica.Domain.Products;

public sealed class ProductReplica
{
    private ProductReplica()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public decimal Price { get; private set; }

    public long Version { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset SyncedAt { get; private set; }
}
