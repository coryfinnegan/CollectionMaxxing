using System.Net;
using System.Net.Sockets;

namespace CollectionMaxxing.Infrastructure.Providers.Fetching;

/// <summary>What a guarded fetch may touch (docs/08, "OWASP API Security Top 10", API7).</summary>
public sealed record FetchPolicy
{
    /// <summary>The cover and artwork hosts, https only on the default port, public addresses only.</summary>
    public static FetchPolicy Covers { get; } = new() { AllowHost = CoverHosts.IsAllowedHost };

    /// <summary>True for the hosts a URL may name (and be redirected to).</summary>
    public Func<string, bool> AllowHost { get; init; } = _ => false;

    /// <summary>Plain http and other ports. Off; only tests that talk to a loopback fake turn it on.</summary>
    public bool AllowHttp { get; init; }

    /// <summary>True for an address the server may connect to. Checked on every address a name resolves to, and on literal addresses.</summary>
    public Func<IPAddress, bool> AddressAllowed { get; init; } = IsPublic;

    public int MaxRedirects { get; init; } = 3;

    public int MaxBytes { get; init; } = 5 * 1024 * 1024;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// True for an address on the public internet. Not loopback, not private (10/8, 172.16/12, 192.168/16, fc00::/7), not link-local
    /// (169.254/16, fe80::/10, which includes cloud metadata), not shared address space (100.64/10, which is Tailscale), not unspecified, multicast
    /// or broadcast. An IPv4 address wrapped in IPv6 is judged as the IPv4 address.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);
        }

        var v4 = address.GetAddressBytes();
        return v4[0] switch
        {
            0 or 10 or 127 => false,
            100 => v4[1] is < 64 or > 127,
            169 => v4[1] != 254,
            172 => v4[1] is < 16 or > 31,
            192 => v4[1] != 168,
            >= 224 => false,
            _ => true,
        };
    }
}

public enum FetchOutcome
{
    Ok,

    /// <summary>The address, scheme, port, user info or host is not allowed (at the start, in a redirect, or after a name was resolved).</summary>
    Refused,

    TooManyRedirects,

    TooLarge,

    /// <summary>The far end said no, was unreachable or took too long.</summary>
    Failed,
}

public sealed record FetchResult(FetchOutcome Outcome, byte[]? Bytes = null, string? ContentType = null);

/// <summary>
/// Fetches one image the way the server is willing to: every URL, including every redirect, is checked against the <see cref="FetchPolicy"/>
/// before a request is made, the connection itself refuses non-public addresses (so a name that resolves to a private address, or changes its
/// answer between the check and the connect, still gets nowhere), redirects are followed by hand and capped, and the body is capped.
/// </summary>
public sealed class GuardedFetcher : IDisposable
{
    private readonly FetchPolicy _policy;
    private readonly HttpClient _client;

    public GuardedFetcher(FetchPolicy policy)
    {
        _policy = policy;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = policy.Timeout,
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = policy.Timeout, MaxResponseContentBufferSize = policy.MaxBytes };
    }

    public async Task<FetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var hop = 0; hop <= _policy.MaxRedirects; hop++)
        {
            if (!Permitted(current))
            {
                return new FetchResult(FetchOutcome.Refused);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not { } location)
                    {
                        return new FetchResult(FetchOutcome.Failed);
                    }

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new FetchResult(FetchOutcome.Failed);
                }

                if (response.Content.Headers.ContentLength > _policy.MaxBytes)
                {
                    return new FetchResult(FetchOutcome.TooLarge);
                }

                return await ReadCappedAsync(response, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.InnerException is RefusedAddressException)
            {
                return new FetchResult(FetchOutcome.Refused);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new FetchResult(FetchOutcome.Failed);
            }
        }

        return new FetchResult(FetchOutcome.TooManyRedirects);
    }

    public void Dispose() => _client.Dispose();

    private bool Permitted(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var schemeOk = uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort || (_policy.AllowHttp && uri.Scheme is "http" or "https");
        if (!schemeOk)
        {
            return false;
        }

        if (!_policy.AllowHost(uri.IdnHost))
        {
            return false;
        }

        // A literal address in the URL is judged right here, before any request.
        return !IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal) || _policy.AddressAllowed(literal);
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);

        // Every answer must be allowed: a name with one public and one private address is not trusted.
        if (addresses.Length == 0 || !addresses.All(_policy.AddressAllowed))
        {
            throw new RefusedAddressException();
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<FetchResult> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > _policy.MaxBytes)
            {
                return new FetchResult(FetchOutcome.TooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        return new FetchResult(FetchOutcome.Ok, buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
    }

    private sealed class RefusedAddressException : Exception
    {
        public RefusedAddressException()
            : base("The address is not one the server may connect to.")
        {
        }
    }
}
