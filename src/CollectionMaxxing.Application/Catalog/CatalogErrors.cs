using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>Failures of the catalogue use cases.</summary>
public static class CatalogErrors
{
    /// <summary>The release doesn't exist, or it belongs to a library the caller isn't in. The two look the same on purpose.</summary>
    public static readonly DomainError ReleaseNotFound =
        new(ErrorKind.NotFound, "release.not_found", "That release doesn't exist.");

    /// <summary>The asset doesn't exist, is private to another library, or its file is missing. All look the same.</summary>
    public static readonly DomainError CoverNotFound =
        new(ErrorKind.NotFound, "cover.not_found", "That image doesn't exist.");

    public static DomainError Invalid(string code, string message, string field) =>
        new(ErrorKind.Validation, code, message, field);
}
