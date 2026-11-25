using System.Security.Cryptography;
using System.Text;
using ConvertApi.Core.Webhooks;

namespace ConvertApi.UnitTests.Webhooks;

public sealed class WebhookSignatureTests
{
    private const string Secret = "whsec-test-0123456789abcdefghijklmnop";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"id\":\"42\",\"status\":\"Succeeded\"}");

    [Fact]
    public void TheHeader_SignsTheTimestampAndTheBody()
    {
        var header = WebhookSignature.Create(Secret, timestamp: 1_795_000_000, Body);

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes("1795000000." + Encoding.UTF8.GetString(Body))));
        Assert.Equal($"t=1795000000,v1={expected}", header);
    }

    [Fact]
    public void AnotherBodyOrTime_GivesAnotherSignature()
    {
        var original = WebhookSignature.Create(Secret, 1_795_000_000, Body);

        Assert.NotEqual(original, WebhookSignature.Create(Secret, 1_795_000_001, Body));
        Assert.NotEqual(original, WebhookSignature.Create(Secret, 1_795_000_000, [.. Body, (byte)' ']));
        Assert.NotEqual(original, WebhookSignature.Create(Secret + "x", 1_795_000_000, Body));
    }
}
