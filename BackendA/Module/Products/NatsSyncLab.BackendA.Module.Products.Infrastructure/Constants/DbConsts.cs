namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Constants;

public static class DbConsts
{
    public const string Schema = "products";
    public const string MigrationHistoryTable = "migration_history_products";
    public const string ProductsTable = "products";
    public const string OutboxTable = "outbox";
}
