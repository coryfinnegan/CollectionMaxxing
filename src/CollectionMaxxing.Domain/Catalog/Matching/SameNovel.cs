using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>
/// Same-novel identity for books (memo 18, "Data mapping", like <c>ProviderFallbackKey</c>): the kind plus the
/// normalized title and creators, with no year — one provider's work records split what is still one novel
/// (issue #514: Open Library keeps Lisa Kleypas's "Suddenly You" under several work keys). A same-title book
/// by a different author is a different work and never matches. Films, TV and games always stay apart here:
/// remakes, ports and re-releases share titles and makers across genuinely different works.
/// </summary>
public static class SameNovel
{
    /// <summary>
    /// True when both sides are the same novel: books with the same normalized title and the same non-empty
    /// set of normalized authors. Anything unverifiable (another kind, a blank title, no author on either
    /// side) is not the same novel: the caller falls back to today's behaviour instead of guessing.
    /// </summary>
    public static bool IsSame(
        WorkKind leftKind, string? leftTitle, IEnumerable<string>? leftCreators,
        WorkKind rightKind, string? rightTitle, IEnumerable<string>? rightCreators)
    {
        if (leftKind != WorkKind.Book || rightKind != WorkKind.Book)
        {
            return false;
        }

        var left = TitleNormalizer.Normalize(leftTitle);
        var right = TitleNormalizer.Normalize(rightTitle);
        if (left.Length == 0 || left != right)
        {
            return false;
        }

        var leftAuthors = Authors(leftCreators);
        var rightAuthors = Authors(rightCreators);
        return leftAuthors.Count > 0 && leftAuthors.SetEquals(rightAuthors);
    }

    private static HashSet<string> Authors(IEnumerable<string>? creators)
    {
        var authors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var creator in creators ?? [])
        {
            var normalized = TitleNormalizer.Normalize(creator);
            if (normalized.Length > 0)
            {
                authors.Add(normalized);
            }
        }

        return authors;
    }
}
