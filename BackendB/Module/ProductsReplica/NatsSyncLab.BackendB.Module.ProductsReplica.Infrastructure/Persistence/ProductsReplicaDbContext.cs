using Microsoft.EntityFrameworkCore;
using NatsSyncLab.BackendB.Module.ProductsReplica.Domain.Products;
using NatsSyncLab.SharedKernel.Persistence;

namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Persistence;

public sealed class ProductsReplicaDbContext(DbContextOptions<ProductsReplicaDbContext> options) : DbContext(options)
{
    public DbSet<ProductReplica> Products => Set<ProductReplica>();

    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProductsReplicaDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
