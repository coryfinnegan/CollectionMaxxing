using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace CollectionMaxxing.Api.Web;

/// <summary>
/// Serves the exported web app (<c>expo export --platform web</c>, copied into <c>wwwroot</c> by the image build) from the API's own origin,
/// so <c>https://pc.tailnet.ts.net/</c> opens the library. <c>app.UseWebApp()</c> goes after the security headers (every file carries them)
/// and answers only a GET or HEAD that no endpoint matched, so the API routes and the sign-in pages are never shadowed. With no export in
/// <c>wwwroot</c> (dev) it does nothing and the API behaves as before.
/// </summary>
public static class WebApp
{
    /// <summary>Paths that belong to the API; an unknown one under these is a plain 404, never the app shell.</summary>
    private static readonly string[] ReservedPrefixes =
        ["/api", "/connect", "/.well-known", "/account", "/signin-oidc", "/media", "/health", "/scan-sessions", "/setup", "/join"];

    private const string ImmutableCache = "public, max-age=31536000, immutable";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IServiceCollection AddWebApp(this IServiceCollection services) => services;

    public static IApplicationBuilder UseWebApp(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint() is null && IsRead(context.Request.Method) && !IsReserved(context.Request.Path))
            {
                var files = context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider;
                if (Resolve(files, context.Request.Path) is { } file)
                {
                    await Send(context, file);
                    return;
                }

                // With the app installed, a file that isn't there is a plain 404 (not the sign-in challenge the API gives an unknown path).
                if (files.GetFileInfo("index.html").Exists)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }

            await next(context);
        });

    private static bool IsRead(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    private static bool IsReserved(PathString path) => ReservedPrefixes.Any(prefix => path.StartsWithSegments(prefix));

    private static (IFileInfo Info, string Path)? Resolve(IFileProvider files, PathString path)
    {
        var parts = (path.Value ?? "/").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".." || p.Contains('\\') || p.Contains(':') || p.Contains('\0')))
        {
            return null;
        }

        var relative = string.Join('/', parts);
        var last = parts.Length == 0 ? "" : parts[^1];

        // A name with an extension is a file: serve it or let it 404. A missing script or image must never come back as HTML.
        if (last.Contains('.', StringComparison.Ordinal))
        {
            return Existing(files, relative);
        }

        // A route: the export has one HTML file per route (library.html, me/index.html, item/[id]/index.html); a dynamic segment is a "[name]" entry.
        return Route(files, parts) ?? Existing(files, "index.html");
    }

    private static (IFileInfo, string)? Existing(IFileProvider files, string relative)
    {
        var info = files.GetFileInfo(relative);
        return info.Exists && !info.IsDirectory ? (info, relative) : null;
    }

    private static (IFileInfo, string)? Route(IFileProvider files, string[] parts)
    {
        var directory = "";
        for (var i = 0; i < parts.Length; i++)
        {
            var isLast = i == parts.Length - 1;
            var literal = Join(directory, parts[i]);
            if (isLast)
            {
                return Existing(files, literal + ".html") ?? Existing(files, Join(literal, "index.html"))
                    ?? Dynamic(files, directory, last: true);
            }

            if (files.GetDirectoryContents(literal).Exists)
            {
                directory = literal;
                continue;
            }

            if (Dynamic(files, directory, last: false) is not { } next)
            {
                return null;
            }

            directory = next.Path;
        }

        return Existing(files, Join(directory, "index.html"));
    }

    // Looks in <directory> for a "[param]" file (last segment) or folder (a middle segment); the catch-all "[...x]" entries are not routes the app uses.
    private static (IFileInfo Info, string Path)? Dynamic(IFileProvider files, string directory, bool last)
    {
        foreach (var entry in files.GetDirectoryContents(directory).Where(e => e.Name.StartsWith('[') && !e.Name.StartsWith("[...", StringComparison.Ordinal)))
        {
            var full = Join(directory, entry.Name);
            if (last && !entry.IsDirectory && entry.Name.EndsWith(".html", StringComparison.Ordinal))
            {
                return (entry, full);
            }

            if (entry.IsDirectory)
            {
                if (!last)
                {
                    return (entry, full);
                }

                if (Existing(files, Join(full, "index.html")) is { } index)
                {
                    return index;
                }
            }
        }

        return null;
    }

    private static string Join(string directory, string name) => directory.Length == 0 ? name : directory + "/" + name;

    private static async Task Send(HttpContext context, (IFileInfo Info, string Path) file)
    {
        var response = context.Response;
        var isHtml = file.Path.EndsWith(".html", StringComparison.Ordinal);
        response.ContentType = isHtml
            ? "text/html; charset=utf-8"
            : ContentTypes.TryGetContentType(file.Path, out var type) ? type : "application/octet-stream";

        // Expo names its scripts and assets by content hash, so they never change under the same name; the HTML does.
        response.Headers.CacheControl = file.Path.StartsWith("_expo/static/", StringComparison.Ordinal) ? ImmutableCache : "no-cache";
        response.ContentLength = file.Info.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await response.SendFileAsync(file.Info, context.RequestAborted);
        }
    }
}
