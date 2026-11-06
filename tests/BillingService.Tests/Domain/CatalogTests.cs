using BillingService.Domain.Entities;
using BillingService.Domain.Exceptions;
using BillingService.Tests.Helpers;

namespace BillingService.Tests.Domain;

public class ServiceTests
{
    [Fact]
    public void Create_ShouldTrimAndStartActive()
    {
        var service = Service.Create("  Alignment  ", null, 90m, TestData.Now);

        Assert.Equal("Alignment", service.Name);
        Assert.Equal(string.Empty, service.Description);
        Assert.False(service.Inactive);
        Assert.Equal(TestData.Now, service.CreatedAt);
        Assert.NotEqual(Guid.Empty, service.Id);
    }

    [Theory]
    [InlineData(" ", 10)]
    [InlineData("Alignment", -1)]
    public void Create_WithInvalidData_ShouldThrow(string name, int price) =>
        Assert.Throws<DomainException>(() => Service.Create(name, null, price, TestData.Now));

    [Fact]
    public void Update_ShouldChangeDataAndValidate()
    {
        var service = TestData.NewService();

        service.Update("Balancing", " Wheels ", 60m);

        Assert.Equal("Balancing", service.Name);
        Assert.Equal("Wheels", service.Description);
        Assert.Equal(60m, service.DefaultPrice);
        Assert.Throws<DomainException>(() => service.Update("", null, 1m));
        Assert.Throws<DomainException>(() => service.Update("Balancing", null, -1m));
    }

    [Fact]
    public void SoftDelete_ShouldInactivate()
    {
        var service = TestData.NewService();

        service.SoftDelete();

        Assert.True(service.Inactive);
    }
}

public class PartTests
{
    [Fact]
    public void Create_ShouldSetStockAndTrim()
    {
        var part = Part.Create(" Oil filter ", " OF-1 ", 5, 30m, TestData.Now);

        Assert.Equal("Oil filter", part.Name);
        Assert.Equal("OF-1", part.Code);
        Assert.Equal(5, part.QuantityInStock);
        Assert.False(part.Inactive);
    }

    [Theory]
    [InlineData(" ", "C", 1, 10)]
    [InlineData("Name", " ", 1, 10)]
    [InlineData("Name", "C", 1, -1)]
    [InlineData("Name", "C", -1, 10)]
    public void Create_WithInvalidData_ShouldThrow(string name, string code, int quantity, int price) =>
        Assert.Throws<DomainException>(() => Part.Create(name, code, quantity, price, TestData.Now));

    [Fact]
    public void Update_ShouldChangeDataAndValidate()
    {
        var part = TestData.NewPart();

        part.Update("Disc", "D-1", 200m);

        Assert.Equal("Disc", part.Name);
        Assert.Equal("D-1", part.Code);
        Assert.Equal(200m, part.Price);
        Assert.Throws<DomainException>(() => part.Update("Disc", "", 1m));
    }

    [Fact]
    public void AddAndRemoveStock_ShouldChangeQuantity()
    {
        var part = TestData.NewPart(stock: 2);

        part.AddStock(3);
        part.RemoveStock(5);

        Assert.Equal(0, part.QuantityInStock);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddStock_WithNonPositiveQuantity_ShouldThrow(int quantity)
    {
        var ex = Assert.Throws<DomainException>(() => TestData.NewPart().AddStock(quantity));

        Assert.Equal("The quantity must be greater than zero.", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void RemoveStock_WithInvalidQuantityOrInsufficientStock_ShouldThrow(int quantity)
    {
        var ex = Assert.Throws<DomainException>(() => TestData.NewPart(stock: 10).RemoveStock(quantity));

        Assert.Equal("Insufficient stock or invalid quantity.", ex.Message);
    }

    [Fact]
    public void SoftDelete_ShouldInactivate()
    {
        var part = TestData.NewPart();

        part.SoftDelete();

        Assert.True(part.Inactive);
    }
}
