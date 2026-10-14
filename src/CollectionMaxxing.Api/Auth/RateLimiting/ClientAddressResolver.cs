using System.Net;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth;

/// <summary>Settings under <c>Auth:RateLimit:</c> that are about who is calling (the limits themselves are in <c>AuthOptions</c>).</summary>
public sealed class TrustedProxyOptions
{
    public const string SectionName = "Auth:RateLimit";

    /// <summary>
    /// Addresses (<c>127.0.0.1</c>) or ranges (<c>172.16.0.0/12</c>) of the reverse proxies whose <c>X-Forwarded-For</c> may be believed:
    /// Tailscale Serve, as the API sees it. Empty (the default) means nothing is trusted and every caller is judged by the address
    /// the connection came from. A comma-separated list in one value works too, so one environment variable can carry several.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];
}

/// <summary>
/// Works out which client a request really came from, for the rate limits and the pairing lockout. The connection's own address is
/// the truth, except when it is a configured trusted proxy: then (and only then) the <c>X-Forwarded-For</c> chain is read, from the
/// right, and the first address that is not itself a trusted proxy is the client. Everything left of that is whatever the client
/// chose to send, so it is never used. A caller who isn't a trusted proxy can put anything in the header and it changes nothing.
/// </summary>
public sealed class ClientAddressResolver
{
    public const string UnknownCaller = "unknown";

    private const string ForwardedFor = "X-Forwarded-For";

    private readonly IPNetwork[] _trusted;

    public ClientAddressResolver(IOptions<TrustedProxyOptions> options)
    {
        _trusted = [.. (options.Value.TrustedProxies ?? []).SelectMany(Split).Select(Parse)];
    }

    /// <summary>The caller's address as text, a stable key for a rate-limit bucket.</summary>
    public string Resolve(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } connection)
        {
            return UnknownCaller;
        }

        var peer = Normalize(connection);
        if (!IsTrusted(peer) || !context.Request.Headers.TryGetValue(ForwardedFor, out var header))
        {
            return peer.ToString();
        }

        var chain = header.SelectMany(line => (line ?? string.Empty).Split(',')).Select(entry => entry.Trim()).ToArray();
        for (var i = chain.Length - 1; i >= 0; i--)
        {
            if (!TryParseAddress(chain[i], out var hop))
            {
                // A hop we can't read: the chain can't be trusted from here on, so the proxy itself is the best answer.
                return peer.ToString();
            }

            hop = Normalize(hop);
            if (!IsTrusted(hop))
            {
                return hop.ToString();
            }
        }

        return peer.ToString();
    }

    /// <summary>Whether every configured entry is an address or a range (checked at start-up, so a typo is loud).</summary>
    public static bool EntriesAreValid(TrustedProxyOptions options) =>
        (options.TrustedProxies ?? []).All(entry => Split(entry).All(part => IPNetwork.TryParse(part, out _) || IPAddress.TryParse(part, out _)));

    private bool IsTrusted(IPAddress address) => _trusted.Any(network => network.Contains(address));

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string[] Split(string? entry) =>
        (entry ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IPNetwork Parse(string entry)
    {
        if (IPNetwork.TryParse(entry, out var network))
        {
            return network;
        }

        if (IPAddress.TryParse(entry, out var single))
        {
            single = Normalize(single);
            return new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        }

        throw new InvalidOperationException("Auth:RateLimit:TrustedProxies has an entry that is not an IP address or a range.");
    }

    private static bool TryParseAddress(string text, out IPAddress address)
    {
        if (IPAddress.TryParse(text, out address!))
        {
            return true;
        }

        // Some proxies add the client's port: 203.0.113.5:49152 or [2001:db8::1]:49152.
        if (text.StartsWith('[') && text.IndexOf(']') is > 0 and var close)
        {
            return IPAddress.TryParse(text[1..close], out address!);
        }

        var colon = text.LastIndexOf(':');
        return colon > 0 && text.IndexOf(':') == colon && IPAddress.TryParse(text[..colon], out address!);
    }
}
