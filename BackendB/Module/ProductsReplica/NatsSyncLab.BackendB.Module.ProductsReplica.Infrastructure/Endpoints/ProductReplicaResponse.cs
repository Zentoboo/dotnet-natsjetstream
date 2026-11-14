namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Endpoints;

internal sealed record ProductReplicaResponse(
    Guid Id,
    string Name,
    decimal Price,
    long Version,
    bool IsDeleted,
    DateTime UpdatedAt,
    DateTime SyncedAt);
