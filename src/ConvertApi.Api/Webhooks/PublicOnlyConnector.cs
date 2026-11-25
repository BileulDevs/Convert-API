using System.Net;
using System.Net.Sockets;
using ConvertApi.Core.Webhooks;

namespace ConvertApi.Api.Webhooks;

/// <summary>
/// Opens webhook connections to public addresses only. The host is resolved here, once, and the
/// socket connects to the very address that was checked: a name that later resolves elsewhere
/// (DNS rebinding) cannot redirect the request into the server's network.
/// </summary>
public static class PublicOnlyConnector
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Each public address in turn: a dual-stack host may have an IPv6 address this server cannot reach.
        SocketException? lastError = null;
        foreach (var address in await ResolveAsync(context.DnsEndPoint.Host, cancellationToken))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                lastError = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException($"No public address of '{context.DnsEndPoint.Host}' accepted the connection.", lastError);
    }

    /// <summary>The public addresses of <paramref name="host"/>; any other address it resolves to is dropped.</summary>
    /// <exception cref="HttpRequestException">The host has no public address.</exception>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        var reachable = addresses.Where(CallbackUrl.IsPublic).ToArray();
        return reachable.Length > 0
            ? reachable
            : throw new HttpRequestException($"The callback host '{host}' does not resolve to a public address.");
    }
}
