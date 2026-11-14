using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NatsSyncLab.BackendA.Module.Products.Domain.Products;
using NatsSyncLab.BackendA.Module.Products.Infrastructure.Constants;

namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Persistence.Configuration;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable(DbConsts.ProductsTable, DbConsts.Schema);

        builder.HasKey(product => product.Id);
        builder.Property(product => product.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => ProductId.Of(value))
            .ValueGeneratedNever();

        builder.Property(product => product.Name)
            .HasColumnName("name")
            .HasMaxLength(Product.NameMaxLength)
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
    }
}
