namespace CollectionMaxxing.Infrastructure.Providers;

/// <summary>
/// The only hosts a cover image may be fetched from (docs/08, "Upload pipeline"). A provider release carries a cover URL
/// only when it is an https address on one of these hosts; anything else is dropped, never fetched. Open Library's cover
/// service is a redirector: /b/id/… answers 302 to archive.org, which 302s to one of its item servers
/// (iaNNNNNN.us.archive.org), so those hops are allow-listed too. Every hop is still fetched over https from a public
/// address, capped in size and kept only when its first bytes are a JPEG, PNG or WebP.
/// </summary>
public static class CoverHosts
{
    private static readonly string[] Allowed =
    [
        "covers.openlibrary.org",
        "archive.org",
        "books.google.com",
        "books.googleusercontent.com",
        "image.tmdb.org",
        "images.igdb.com",
    ];

    /// <summary>Host suffixes any subdomain of which is allowed (Internet Archive item servers).</summary>
    private static readonly string[] AllowedSuffixes = [".us.archive.org"];

    /// <summary>True when <paramref name="host"/> is one of the cover hosts.</summary>
    public static bool IsAllowedHost(string host) =>
        Allowed.Contains(host, StringComparer.OrdinalIgnoreCase)
        || AllowedSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The URL as an https address on an allow-listed host, or null.</summary>
    public static string? Allow(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return null;
        }

        if (!IsAllowedHost(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
    }
}
