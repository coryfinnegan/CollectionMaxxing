namespace CollectionMaxxing.Infrastructure.Providers;

/// <summary>
/// The only hosts a cover image may be fetched from (docs/08, "Upload pipeline"). A provider release carries a cover URL
/// only when it is an https address on one of these hosts; anything else is dropped, never fetched.
/// </summary>
public static class CoverHosts
{
    private static readonly string[] Allowed =
    [
        "covers.openlibrary.org",
        "books.google.com",
        "books.googleusercontent.com",
        "image.tmdb.org",
        "images.igdb.com",
    ];

    /// <summary>The URL as an https address on an allow-listed host, or null.</summary>
    public static string? Allow(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return null;
        }

        if (!Allowed.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
    }
}
