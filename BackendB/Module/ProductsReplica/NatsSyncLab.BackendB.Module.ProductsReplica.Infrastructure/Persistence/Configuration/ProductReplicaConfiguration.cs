using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NatsSyncLab.BackendB.Module.ProductsReplica.Domain.Products;
using NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Constants;

namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Persistence.Configuration;

internal sealed class ProductReplicaConfiguration : IEntityTypeConfiguration<ProductReplica>
{
    public void Configure(EntityTypeBuilder<ProductReplica> builder)
    {
        builder.ToTable(DbConsts.ProductsTable, DbConsts.Schema);

        builder.HasKey(product => product.Id);
        builder.Property(product => product.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(product => product.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(product => product.Price)
            .HasColumnName("price")
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(product => product.Version)
            .HasColumnName("version")
            .IsRequired();

        builder.Property(product => product.IsDeleted)
            .HasColumnName("is_deleted")
            .IsRequired();

        builder.Property(product => product.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(product => product.SyncedAt)
            .HasColumnName("synced_at")
            .IsRequired();
    }
}
