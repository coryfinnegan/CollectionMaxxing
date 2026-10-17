using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Catalog.FeaturePrints;

/// <summary>Why a feature-print request was refused, and the limits it enforces.</summary>
public static class FeaturePrintErrors
{
    /// <summary>The most prints one <c>PUT</c> may carry.</summary>
    public const int MaxBatch = 200;

    /// <summary>The most bytes of print data one print may carry (after decoding). Apple's prints are about 2 to 8 KB.</summary>
    public const int MaxDataBytes = 16 * 1024;

    /// <summary>The most prints one page of the list holds.</summary>
    public const int MaxPage = 200;

    public static readonly DomainError ItemNotFound = new(ErrorKind.NotFound, "item.not_found", "One of those items doesn't exist in this library.");

    public static readonly DomainError ReleaseNotFound = new(ErrorKind.NotFound, "release.not_found", "One of those releases doesn't exist.");

    public static DomainError Invalid(string code, string message, string field) => new(ErrorKind.Validation, code, message, field);

    public static readonly DomainError BadCursor = Invalid("featureprints.cursor_invalid", "That cursor isn't valid.", "cursor");
}
