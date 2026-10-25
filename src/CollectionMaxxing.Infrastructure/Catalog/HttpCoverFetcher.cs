using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Infrastructure.Providers.Fetching;

namespace CollectionMaxxing.Infrastructure.Catalog;

/// <summary>
/// Downloads provider covers through <see cref="GuardedFetcher"/> (allow-listed hosts only). What comes back is trusted by its first bytes,
/// not by the server's Content-Type: only a JPEG, PNG or WebP is kept. Anything else comes back as a failure reason.
/// </summary>
public sealed class HttpCoverFetcher : ICoverFetcher, IDisposable
{
    private readonly GuardedFetcher _fetcher;

    /// <param name="policy">The fetch policy; null keeps the production cover policy. Tests pass one that allows loopback fakes.</param>
    public HttpCoverFetcher(FetchPolicy? policy = null)
    {
        _fetcher = new GuardedFetcher(policy ?? FetchPolicy.Covers);
    }

    public async Task<CoverFetchResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return new CoverFetchResult(null, CoverFailure.InvalidUrl);
        }

        var result = await _fetcher.FetchAsync(uri, cancellationToken);
        if (result is not { Outcome: FetchOutcome.Ok, Bytes: { } bytes })
        {
            return new CoverFetchResult(null, ReasonOf(result.Outcome));
        }

        return SniffType(bytes) is { } type
            ? new CoverFetchResult(new FetchedCover(bytes, type), null)
            : new CoverFetchResult(null, CoverFailure.NotAnImage);
    }

    public void Dispose() => _fetcher.Dispose();

    private static string ReasonOf(FetchOutcome outcome) => outcome switch
    {
        FetchOutcome.Refused => CoverFailure.Refused,
        FetchOutcome.TooManyRedirects => CoverFailure.TooManyRedirects,
        FetchOutcome.TooLarge => CoverFailure.TooLarge,
        _ => CoverFailure.FetchFailed,
    };

    private static string? SniffType(byte[] b) => b switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        _ => null,
    };
}
