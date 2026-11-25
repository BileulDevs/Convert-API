using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace ConvertApi.Core.Webhooks;

/// <summary>
/// Which URLs a job may notify. The server calls them itself, so a client must not be able to
/// aim it at the server's own network (SSRF): only HTTPS to public addresses is allowed.
/// </summary>
/// <remarks>
/// A host name is only checked here for obvious local names. What it resolves to can change at
/// any time (DNS rebinding), so the delivery checks <see cref="IsPublic"/> again on the address
/// it actually connects to.
/// </remarks>
public static class CallbackUrl
{
    public const int MaxLength = 2048;

    public static bool TryParse(
        string value, [NotNullWhen(true)] out Uri? url, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(value);

        url = null;
        error = Check(value, out var parsed);
        if (error is null)
        {
            url = parsed!;
        }

        return error is null;
    }

    /// <summary>Whether <paramref name="url"/> passes <see cref="TryParse"/>.</summary>
    public static bool IsAllowed(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return Check(url.OriginalString, out _) is null;
    }

    /// <summary>
    /// Whether an address is on the public internet: not loopback, private, link-local, shared,
    /// multicast, reserved or documentation, including those hidden in IPv6 forms.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(bytes),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(bytes),
            _ => false,
        };
    }

    private static string? Check(string value, out Uri? url)
    {
        url = null;
        if (value.Length > MaxLength)
        {
            return $"callbackUrl is limited to {MaxLength} characters.";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            return "callbackUrl must be an absolute https:// URL.";
        }

        if (parsed.UserInfo.Length > 0)
        {
            return "callbackUrl must not contain credentials.";
        }

        if (!IsPublicHost(parsed))
        {
            return "callbackUrl must point to a public internet host, not a local or private one.";
        }

        url = parsed;
        return null;
    }

    /// <remarks>
    /// Works on <see cref="Uri.IdnHost"/>, the name the HTTP client connects to: "１２７.0.0.1" is typed
    /// as a DNS name but becomes 127.0.0.1 there. A trailing dot is ignored, as resolvers do.
    /// </remarks>
    private static bool IsPublicHost(Uri url)
    {
        if (url.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || url.IdnHost.Contains('%', StringComparison.Ordinal))
        {
            return false; // IPv6 zone ids name a local interface
        }

        var host = url.IdnHost.Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(host, out var address))
        {
            return IsPublic(address);
        }

        // A single-label name ("intranet") resolves through the server's search domains, so to an
        // internal host; an all-digit or hex last label is an address in disguise, never a real TLD.
        var lastDot = host.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return false;
        }

        var topLabel = host[(lastDot + 1)..];
        return topLabel.Length > 0
            && !topLabel.All(char.IsAsciiDigit)
            && !topLabel.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublicIPv4(byte[] b) => !(
        b[0] is 0 or 10 or 127 or >= 224                // this network, private, loopback, multicast and reserved
        || (b[0] == 100 && b[1] is >= 64 and <= 127)    // shared address space (carrier-grade NAT)
        || (b[0] == 169 && b[1] == 254)                 // link-local, cloud metadata endpoints
        || (b[0] == 172 && b[1] is >= 16 and <= 31)     // private
        || (b[0] == 192 && b[1] == 168)                 // private
        || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) // protocol assignments, documentation
        || (b[0] == 198 && b[1] is 18 or 19)            // benchmarking
        || (b[0] == 198 && b[1] == 51 && b[2] == 100)   // documentation
        || (b[0] == 203 && b[1] == 0 && b[2] == 113));  // documentation

    /// <summary>
    /// Only global unicast (2000::/3), minus ranges that embed or reach an IPv4 address the check
    /// above would refuse (6to4, Teredo), benchmarking and documentation. NAT64, ULA, link-local
    /// and the rest fall outside 2000::/3.
    /// </summary>
    private static bool IsPublicIPv6(byte[] b) =>
        (b[0] & 0xE0) == 0x20
        && !(b[0] == 0x20 && b[1] == 0x02)                                     // 6to4
        && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)           // Teredo
        && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0x02 && b[4] == 0 && b[5] == 0) // benchmarking
        && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)     // documentation
        && !(b[0] == 0x3F && (b[1] & 0xF0) == 0xF0);                           // documentation (3fff::/20)
}
