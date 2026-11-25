using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ConvertApi.Core.Webhooks;

/// <summary>
/// Signs a webhook so its receiver can tell it comes from this API and was not replayed:
/// <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 of "t.body" with the key's webhook secret&gt;</c>.
/// </summary>
/// <remarks>
/// A receiver checks a notification by recomputing the HMAC over the raw body with its secret (the
/// UTF-8 string as given, not base64-decoded) and comparing in constant time; it refuses a timestamp
/// more than about 5 minutes from its own clock. Delivery is at least once: it ignores a job id and
/// status it already handled.
/// </remarks>
public static class WebhookSignature
{
    public static string Create(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));

        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        return string.Create(CultureInfo.InvariantCulture, $"t={timestamp},v1={Convert.ToHexStringLower(mac)}");
    }
}
