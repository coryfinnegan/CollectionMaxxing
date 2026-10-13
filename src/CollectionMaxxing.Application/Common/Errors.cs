using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Common;

/// <summary>Failures shared by several features.</summary>
public static class Errors
{
    /// <summary>The library doesn't exist, or the caller can't see it. The two look the same on purpose.</summary>
    public static readonly DomainError LibraryNotFound =
        new(ErrorKind.NotFound, "library.not_found", "That library doesn't exist.");
}
