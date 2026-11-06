using System.Security.Cryptography;
using System.Text;
using BillingService.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BillingService.Infrastructure.MercadoPago;

public class MercadoPagoSignatureValidator(IOptions<MercadoPagoOptions> options) : IWebhookSignatureValidator
{
    private readonly string _secret = options.Value.WebhookSecret;

    public bool IsValid(string? signatureHeader, string? requestId, string? dataId)
    {
        if (string.IsNullOrWhiteSpace(_secret) || string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        string? ts = null;
        string? v1 = null;
        foreach (var part in signatureHeader.Split(',', StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
                continue;
            if (pair[0] == "ts")
                ts = pair[1];
            else if (pair[0] == "v1")
                v1 = pair[1];
        }

        if (string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(v1))
            return false;

        var manifest = new StringBuilder();
        if (!string.IsNullOrEmpty(dataId))
            manifest.Append("id:").Append(dataId).Append(';');
        if (!string.IsNullOrEmpty(requestId))
            manifest.Append("request-id:").Append(requestId).Append(';');
        manifest.Append("ts:").Append(ts).Append(';');

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_secret), Encoding.UTF8.GetBytes(manifest.ToString()));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Convert.ToHexString(expected).ToLowerInvariant()),
            Encoding.ASCII.GetBytes(v1.ToLowerInvariant()));
    }
}
