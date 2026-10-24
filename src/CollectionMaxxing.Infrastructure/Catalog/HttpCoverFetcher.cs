using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Infrastructure.Providers.Fetching;

namespace CollectionMaxxing.Infrastructure.Catalog;

/// <summary>
/// Downloads provider covers through <see cref="GuardedFetcher"/> (allow-listed hosts only). What comes back is trusted by its first bytes,
/// not by the server's Content-Type: only a JPEG, PNG or WebP is kept.
/// </summary>
public sealed class HttpCoverFetcher : ICoverFetcher, IDisposable
{
    private readonly GuardedFetcher _fetcher = new(FetchPolicy.Covers);

    public async Task<FetchedCover?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var result = await _fetcher.FetchAsync(uri, cancellationToken);
        if (result is not { Outcome: FetchOutcome.Ok, Bytes: { } bytes })
        {
            return null;
        }

        return SniffType(bytes) is { } type ? new FetchedCover(bytes, type) : null;
    }

    public void Dispose() => _fetcher.Dispose();

    private static string? SniffType(byte[] b) => b switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        _ => null,
    };
}
