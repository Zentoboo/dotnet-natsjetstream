using NatsSyncLab.BackendA.Module.Products.Domain.Products;
using Xunit;

namespace NatsSyncLab.BackendA.Module.Products.UnitTests;

public sealed class ProductTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_initializes_product_at_version_one()
    {
        var product = Product.Create("  Keyboard  ", 49.95m, Now);

        Assert.NotEqual(Guid.Empty, product.Id.Value);
        Assert.Equal("Keyboard", product.Name);
        Assert.Equal(49.95m, product.Price);
        Assert.Equal(1, product.Version);
        Assert.False(product.IsDeleted);
        Assert.Equal(Now, product.UpdatedAt);
    }

    [Fact]
    public void Update_increments_version_and_changes_values()
    {
        var product = Product.Create("Keyboard", 49.95m, Now);
        var later = Now.AddMinutes(1);

        product.Update("Mechanical Keyboard", 89.95m, later);

        Assert.Equal("Mechanical Keyboard", product.Name);
        Assert.Equal(89.95m, product.Price);
        Assert.Equal(2, product.Version);
        Assert.Equal(later, product.UpdatedAt);
    }

    [Fact]
    public void Delete_is_soft_and_increments_version()
    {
        var product = Product.Create("Keyboard", 49.95m, Now);
        var later = Now.AddMinutes(1);

        var deleted = product.Delete(later);

        Assert.True(deleted);
        Assert.True(product.IsDeleted);
        Assert.Equal(2, product.Version);
        Assert.Equal(later, product.UpdatedAt);
    }

    [Fact]
    public void Delete_twice_returns_false_without_changing_version()
    {
        var product = Product.Create("Keyboard", 49.95m, Now);
        product.Delete(Now.AddMinutes(1));

        var deletedAgain = product.Delete(Now.AddMinutes(2));

        Assert.False(deletedAgain);
        Assert.Equal(2, product.Version);
    }

    [Fact]
    public void Update_after_delete_is_rejected()
    {
        var product = Product.Create("Keyboard", 49.95m, Now);
        product.Delete(Now.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() =>
            product.Update("Keyboard", 49.95m, Now.AddMinutes(2)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_name(string name)
    {
        Assert.Throws<ArgumentException>(() => Product.Create(name, 10m, Now));
    }

    [Fact]
    public void Create_rejects_negative_price()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Product.Create("Keyboard", -1m, Now));
    }
}
