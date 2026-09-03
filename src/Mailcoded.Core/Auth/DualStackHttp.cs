using System.Net.Sockets;
using Mailcoded.Core.Providers;
using Microsoft.Identity.Client;

namespace Mailcoded.Core.Auth;

/// <summary>MSAL's HttpClient, connected the same way the mail sockets are. Without this a machine
/// with AAAA records and no IPv6 route waits out the whole request timeout on every sign-in.</summary>
internal sealed class DualStackHttp : IMsalHttpClientFactory
{
    public static readonly DualStackHttp Factory = new();

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _client = new(
        new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = await DualStackConnector
                    .ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ConnectTimeout, ct)
                    .ConfigureAwait(false);

                return new NetworkStream(socket, ownsSocket: true);
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public HttpClient GetHttpClient() => _client;
}
