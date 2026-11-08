using System.Globalization;
using Amazon.DynamoDBv2.Model;
using BillingService.Domain.Entities;
using BillingService.Domain.Enums;

namespace BillingService.Infrastructure.Persistence;

internal static class BillingItems
{
    public const string ServicePartition = "SERVICE";
    public const string PartPartition = "PART";
    public const string PaymentPartition = "PAYMENT";

    public static Dictionary<string, AttributeValue> ToItem(Service s) => new()
    {
        ["pk"] = S(ServicePartition),
        ["sk"] = S(s.Id.ToString("D")),
        ["entityType"] = S("Service"),
        ["id"] = S(s.Id.ToString("D")),
        ["name"] = S(s.Name),
        ["description"] = S(s.Description),
        ["defaultPrice"] = N(s.DefaultPrice),
        ["inactive"] = Bool(s.Inactive),
        ["createdAt"] = S(Timestamp(s.CreatedAt))
    };

    public static Dictionary<string, AttributeValue> ToItem(Part p) => new()
    {
        ["pk"] = S(PartPartition),
        ["sk"] = S(p.Id.ToString("D")),
        ["entityType"] = S("Part"),
        ["id"] = S(p.Id.ToString("D")),
        ["name"] = S(p.Name),
        ["code"] = S(p.Code),
        ["quantityInStock"] = N(p.QuantityInStock),
        ["price"] = N(p.Price),
        ["inactive"] = Bool(p.Inactive),
        ["createdAt"] = S(Timestamp(p.CreatedAt))
    };

    public static Dictionary<string, AttributeValue> ToItem(PaymentTransaction p)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = S(PaymentPartition),
            ["sk"] = S(p.Id.ToString("D")),
            ["entityType"] = S("PaymentTransaction"),
            ["id"] = S(p.Id.ToString("D")),
            ["serviceOrderId"] = S(p.ServiceOrderId.ToString("D")),
            ["totalAmount"] = N(p.TotalAmount),
            ["gatewayTransactionId"] = S(p.GatewayTransactionId),
            ["paymentUrl"] = S(p.PaymentUrl),
            ["status"] = S(p.Status.ToString()),
            ["createdAt"] = S(Timestamp(p.CreatedAt))
        };

        if (!string.IsNullOrWhiteSpace(p.GatewayPaymentId))
            item["gatewayPaymentId"] = S(p.GatewayPaymentId);

        return item;
    }

    public static Service ToService(Dictionary<string, AttributeValue> item) => Service.Restore(
        Guid.Parse(item["id"].S),
        item["name"].S,
        item.TryGetValue("description", out var description) ? description.S : string.Empty,
        Decimal(item["defaultPrice"]),
        item["inactive"].BOOL,
        Date(item["createdAt"]),
        Version(item));

    public static Part ToPart(Dictionary<string, AttributeValue> item) => Part.Restore(
        Guid.Parse(item["id"].S),
        item["name"].S,
        item["code"].S,
        int.Parse(item["quantityInStock"].N, CultureInfo.InvariantCulture),
        Decimal(item["price"]),
        item["inactive"].BOOL,
        Date(item["createdAt"]),
        Version(item));

    public static PaymentTransaction ToPayment(Dictionary<string, AttributeValue> item) => PaymentTransaction.Restore(
        Guid.Parse(item["id"].S),
        Guid.Parse(item["serviceOrderId"].S),
        Decimal(item["totalAmount"]),
        item["gatewayTransactionId"].S,
        item["paymentUrl"].S,
        Enum.Parse<PaymentStatus>(item["status"].S),
        Date(item["createdAt"]),
        Version(item),
        item.TryGetValue("gatewayPaymentId", out var gatewayPaymentId) ? gatewayPaymentId.S : null);

    public static string Timestamp(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    public static AttributeValue S(string value) => new() { S = value };

    public static AttributeValue N(decimal value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static AttributeValue Bool(bool value) => new() { BOOL = value, IsBOOLSet = true };

    private static decimal Decimal(AttributeValue value) => decimal.Parse(value.N, CultureInfo.InvariantCulture);

    private static DateTime Date(AttributeValue value) =>
        DateTime.Parse(value.S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static int Version(Dictionary<string, AttributeValue> item) =>
        int.Parse(item["version"].N, CultureInfo.InvariantCulture);
}
