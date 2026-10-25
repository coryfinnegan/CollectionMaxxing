namespace CollectionMaxxing.Application.Catalog;

/// <summary>A cover image downloaded from a provider: a sound JPEG, PNG or WebP, not larger than the fetcher's cap.</summary>
public sealed record FetchedCover(ReadOnlyMemory<byte> Bytes, string ContentType);

/// <summary>Why a cover download ended without an image. Stable strings: they are recorded on the release.</summary>
public static class CoverFailure
{
    /// <summary>Not an absolute URL, or a scheme the fetcher never touches.</summary>
    public const string InvalidUrl = "invalid-url";

    /// <summary>The address, scheme, port or host is not allowed (at the start or in a redirect).</summary>
    public const string Refused = "refused";

    public const string TooManyRedirects = "too-many-redirects";

    /// <summary>The image is bigger than the fetcher's cap.</summary>
    public const string TooLarge = "too-large";

    /// <summary>The far end said no, was unreachable or took too long.</summary>
    public const string FetchFailed = "fetch-failed";

    /// <summary>The bytes are no JPEG, PNG or WebP.</summary>
    public const string NotAnImage = "not-an-image";

    /// <summary>The commit's cover budget ran out before this download finished.</summary>
    public const string TimedOut = "timed-out";
}

/// <summary>How one cover download ended: the image, or why there is none.</summary>
public sealed record CoverFetchResult(FetchedCover? Cover, string? FailureReason);

/// <summary>
/// Downloads a cover a provider named. Implemented by Infrastructure over the guarded fetcher (allow-listed hosts, https, public
/// addresses, size cap). A bad or unreachable URL comes back as a failure reason, never an exception; only the caller's own
/// cancellation escapes.
/// </summary>
public interface ICoverFetcher
{
    Task<CoverFetchResult> FetchAsync(string url, CancellationToken cancellationToken);
}
