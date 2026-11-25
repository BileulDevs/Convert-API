using System.Net;
using ConvertApi.Core.Webhooks;

namespace ConvertApi.UnitTests.Webhooks;

public sealed class CallbackUrlTests
{
    [Theory]
    [InlineData("https://example.com/hook")]
    [InlineData("https://hooks.example.com:8443/convert?client=42")]
    [InlineData("https://8.8.8.8/hook")]
    [InlineData("https://example.com./hook")]
    [InlineData("https://[2606:4700:4700::1111]/hook")]
    public void APublicHttpsUrl_IsAccepted(string value)
    {
        Assert.True(CallbackUrl.TryParse(value, out var url, out var error), error);
        Assert.Equal(new Uri(value), url);
    }

    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("/relative/hook")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("https://user:secret@example.com/hook")]
    public void ANonHttpsOrMalformedUrl_IsRefused(string value)
    {
        Assert.False(CallbackUrl.TryParse(value, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("https://localhost/hook")]
    [InlineData("https://api.localhost/hook")]
    [InlineData("https://intranet/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://172.16.0.1/hook")]
    [InlineData("https://192.168.1.1/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://100.64.0.1/hook")]
    [InlineData("https://0.0.0.0/hook")]
    [InlineData("https://2130706433/hook")]
    [InlineData("https://0x7f000001/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[fd00::1]/hook")]
    [InlineData("https://[fe80::1]/hook")]
    [InlineData("https://[::ffff:127.0.0.1]/hook")]
    [InlineData("https://[64:ff9b::a00:1]/hook")]
    [InlineData("https://localhost./hook")]
    [InlineData("https://a.localhost./hook")]
    [InlineData("https://127.0.0.1./hook")]
    [InlineData("https://169.254.169.254./latest/meta-data")]
    [InlineData("https://１２７.0.0.1/hook")]
    [InlineData("https://example.0x7f/hook")]
    [InlineData("https://[2001:4860::1%25eth0]/hook")]
    [InlineData("https://[3fff::1]/hook")]
    public void ALocalOrPrivateTarget_IsRefused(string value)
    {
        Assert.False(CallbackUrl.TryParse(value, out _, out _));
    }

    [Fact]
    public void AnOverlongUrl_IsRefused()
    {
        var value = "https://example.com/" + new string('a', CallbackUrl.MaxLength);

        Assert.False(CallbackUrl.TryParse(value, out _, out _));
    }

    [Theory]
    [InlineData("93.184.215.14", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002:7f00:1::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:93.184.215.14", true)]
    public void IsPublic_ClassifiesAddresses(string address, bool expected)
    {
        Assert.Equal(expected, CallbackUrl.IsPublic(IPAddress.Parse(address)));
    }
}
