using NatsSyncLab.SharedKernel.Abstractions;

namespace NatsSyncLab.BackendA.Module.Products.Domain.Products;

public sealed class Product : ISoftDelete
{
    public const int NameMaxLength = 200;

    private Product()
    {
    }

    public ProductId Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public decimal Price { get; private set; }

    public long Version { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    bool ISoftDelete.IsDeleted
    {
        get => IsDeleted;
        set => IsDeleted = value;
    }

    public static Product Create(string name, decimal price, DateTimeOffset now)
    {
        Validate(name, price);

        return new Product
        {
            Id = ProductId.New(),
            Name = name.Trim(),
            Price = price,
            Version = 1,
            UpdatedAt = now,
        };
    }

    public void Update(string name, decimal price, DateTimeOffset now)
    {
        ThrowIfDeleted();
        Validate(name, price);

        Name = name.Trim();
        Price = price;
        Version++;
        UpdatedAt = now;
    }

    public bool Delete(DateTimeOffset now)
    {
        if (IsDeleted)
        {
            return false;
        }

        IsDeleted = true;
        Version++;
        UpdatedAt = now;
        return true;
    }

    private static void Validate(string name, decimal price)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Trim().Length > NameMaxLength)
        {
            throw new ArgumentException($"Product name cannot exceed {NameMaxLength} characters.", nameof(name));
        }

        if (price < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(price), price, "Product price cannot be negative.");
        }
    }

    private void ThrowIfDeleted()
    {
        if (IsDeleted)
        {
            throw new InvalidOperationException("A deleted product cannot be updated.");
        }
    }
}
