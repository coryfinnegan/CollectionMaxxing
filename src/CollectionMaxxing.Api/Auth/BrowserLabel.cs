namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// A short, friendly name for a signed-in browser ("Edge on Windows"), guessed from its User-Agent. Only words from the fixed lists below are
/// ever stored: the header itself is the browser's to write, so none of it is kept.
/// </summary>
public static class BrowserLabel
{
    public const string Unknown = "Web browser";

    // Order matters: Edge, Opera and Chrome all say "Chrome" and "Safari" too, and every phone says "Mac OS X".
    private static readonly (string Marker, string Name)[] Browsers =
    [
        ("Edg/", "Edge"), ("EdgA/", "Edge"), ("EdgiOS/", "Edge"), ("OPR/", "Opera"), ("FxiOS/", "Firefox"), ("Firefox/", "Firefox"),
        ("CriOS/", "Chrome"), ("Chrome/", "Chrome"), ("Safari/", "Safari"),
    ];

    private static readonly (string Marker, string Name)[] Systems =
    [
        ("iPhone", "iPhone"), ("iPad", "iPad"), ("Android", "Android"), ("Windows", "Windows"), ("CrOS", "ChromeOS"),
        ("Macintosh", "Mac"), ("Mac OS X", "Mac"), ("Linux", "Linux"), ("X11", "Linux"),
    ];

    public static string From(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return Unknown;
        }

        var browser = Browsers.FirstOrDefault(b => userAgent.Contains(b.Marker, StringComparison.Ordinal)).Name;
        var system = Systems.FirstOrDefault(s => userAgent.Contains(s.Marker, StringComparison.Ordinal)).Name;
        return (browser, system) switch
        {
            (null, _) => Unknown,
            (_, null) => browser,
            _ => $"{browser} on {system}",
        };
    }
}
