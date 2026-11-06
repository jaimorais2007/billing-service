using BillingService.Domain.Common;
using BillingService.Domain.Exceptions;

namespace BillingService.Domain.Entities;

public class Part : VersionedEntity
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public int QuantityInStock { get; private set; }
    public decimal Price { get; private set; }
    public bool Inactive { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private Part()
    {
    }

    public static Part Create(string name, string code, int initialQuantity, decimal price, DateTime now)
    {
        Validate(name, code, price);
        if (initialQuantity < 0)
            throw new DomainException("The initial quantity cannot be negative.");

        return new Part
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Code = code.Trim(),
            QuantityInStock = initialQuantity,
            Price = price,
            CreatedAt = now
        };
    }

    public static Part Restore(
        Guid id,
        string name,
        string code,
        int quantityInStock,
        decimal price,
        bool inactive,
        DateTime createdAt,
        int version) => new()
    {
        Id = id,
        Name = name,
        Code = code,
        QuantityInStock = quantityInStock,
        Price = price,
        Inactive = inactive,
        CreatedAt = createdAt,
        Version = version
    };

    public void Update(string name, string code, decimal price)
    {
        Validate(name, code, price);
        Name = name.Trim();
        Code = code.Trim();
        Price = price;
    }

    public void AddStock(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("The quantity must be greater than zero.");

        QuantityInStock += quantity;
    }

    public void RemoveStock(int quantity)
    {
        if (quantity <= 0 || QuantityInStock < quantity)
            throw new DomainException("Insufficient stock or invalid quantity.");

        QuantityInStock -= quantity;
    }

    public void SoftDelete() => Inactive = true;

    private static void Validate(string name, string code, decimal price)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("The part name is required.");
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException("The part code is required.");
        if (price < 0)
            throw new DomainException("The part price cannot be negative.");
    }
}
