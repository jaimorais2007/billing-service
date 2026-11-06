using BillingService.Domain.Common;
using BillingService.Domain.Exceptions;

namespace BillingService.Domain.Entities;

public class Service : VersionedEntity
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public decimal DefaultPrice { get; private set; }
    public bool Inactive { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private Service()
    {
    }

    public static Service Create(string name, string? description, decimal defaultPrice, DateTime now)
    {
        Validate(name, defaultPrice);

        return new Service
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Description = description?.Trim() ?? string.Empty,
            DefaultPrice = defaultPrice,
            CreatedAt = now
        };
    }

    public static Service Restore(
        Guid id,
        string name,
        string description,
        decimal defaultPrice,
        bool inactive,
        DateTime createdAt,
        int version) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        DefaultPrice = defaultPrice,
        Inactive = inactive,
        CreatedAt = createdAt,
        Version = version
    };

    public void Update(string name, string? description, decimal defaultPrice)
    {
        Validate(name, defaultPrice);
        Name = name.Trim();
        Description = description?.Trim() ?? string.Empty;
        DefaultPrice = defaultPrice;
    }

    public void SoftDelete() => Inactive = true;

    private static void Validate(string name, decimal defaultPrice)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("The service name is required.");
        if (defaultPrice < 0)
            throw new DomainException("The service price cannot be negative.");
    }
}
