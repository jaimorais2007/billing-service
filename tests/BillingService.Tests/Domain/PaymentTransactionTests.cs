using BillingService.Domain.Entities;
using BillingService.Domain.Enums;
using BillingService.Domain.Exceptions;
using BillingService.Tests.Helpers;

namespace BillingService.Tests.Domain;

public class PaymentTransactionTests
{
    [Fact]
    public void Create_ShouldStartPending()
    {
        var payment = TestData.NewPayment();

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal("pref-1", payment.GatewayTransactionId);
        Assert.Equal(0, payment.Version);
    }

    [Theory]
    [InlineData(false, 10, "pref", "url")]
    [InlineData(true, 0, "pref", "url")]
    [InlineData(true, 10, " ", "url")]
    [InlineData(true, 10, "pref", " ")]
    public void Create_WithInvalidData_ShouldThrow(bool hasOrder, int amount, string transactionId, string url) =>
        Assert.Throws<DomainException>(() => PaymentTransaction.Create(
            Guid.NewGuid(), hasOrder ? Guid.NewGuid() : Guid.Empty, amount, transactionId, url, TestData.Now));

    [Fact]
    public void Pay_ShouldReturnTrueOnlyOnce()
    {
        var payment = TestData.NewPayment();

        Assert.True(payment.Pay());
        Assert.False(payment.Pay());
        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }

    [Fact]
    public void Pay_AfterRefused_ShouldBeAllowed()
    {
        var payment = TestData.NewPayment();
        payment.Refuse();

        Assert.True(payment.Pay());
    }

    [Fact]
    public void Cancel_ShouldOnlyApplyToPendingAndAllowLatePayment()
    {
        var payment = TestData.NewPayment();

        Assert.True(payment.Cancel());
        Assert.False(payment.Cancel());
        Assert.False(payment.Refuse());
        Assert.Equal(PaymentStatus.Cancelled, payment.Status);

        Assert.True(payment.Pay());
        Assert.False(payment.Cancel());
    }

    [Fact]
    public void IsOpenAt_ShouldConsiderStatusAndLifetime()
    {
        var payment = TestData.NewPayment();
        var lifetime = TimeSpan.FromHours(1);

        Assert.True(payment.IsOpenAt(TestData.Now, lifetime));
        Assert.False(payment.IsOpenAt(TestData.Now.AddHours(1), lifetime));

        payment.Refuse();
        Assert.True(payment.IsOpenAt(TestData.Now, lifetime));

        payment.Pay();
        Assert.False(payment.IsOpenAt(TestData.Now, lifetime));
    }

    [Fact]
    public void Refuse_ShouldOnlyApplyToPending()
    {
        var payment = TestData.NewPayment();

        Assert.True(payment.Refuse());
        Assert.False(payment.Refuse());

        payment.Pay();
        Assert.False(payment.Refuse());
        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }
}
