namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Endpoints;

internal sealed record ProductReadResponse(
    Guid Id,
    string Name,
    decimal Price,
    long Version,
    bool IsDeleted,
    DateTime UpdatedAt);
