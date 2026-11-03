using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// The last thing a spine read that found nothing is asked as: with its creator as the title and its title as the creator. The
/// phone cannot always tell an author line from a title of the same size, so it sometimes sends the author as the title and the
/// real title as the creator (the author is printed as big as the title by a hair, or a neighbour's author is in the piece); the
/// catalogue can tell which is which. The creator is the one the phone sent (<see cref="ReadFacts.CreatorAsRead"/>), not the one
/// the sanitiser kept: it drops an author line with "the" in it, and a real title often has one. Pure; the retry itself is
/// <see cref="IdentifyGrounding.RetryTrimmedAsync"/>, where this is asked after every trimmed span (<see cref="TrimmedSpans"/>).
/// </summary>
public static class CreatorSwap
{
    /// <summary>
    /// The read to ask again, or null when there is nothing worth asking: no title, no creator as read, a creator that says nothing
    /// the title does not (the title again, or words of it) and so is no other title, or one too little to search for (the same
    /// minimum a trimmed span has: <see cref="TrimmedSpans.MinWordsKept"/> words, or one word of <see cref="TrimmedSpans.MinLettersOfOneWord"/>
    /// letters). Otherwise the read with the creator as read for its title and its title for the creator, cleaned as a read is: the old
    /// title is a claim about an author now, and counts only if it reads as a name. Everything else about the read is unchanged
    /// (<see cref="ReadFacts.Text"/> stays what was read); it has no creator as read left, so it cannot be swapped again.
    /// </summary>
    public static ReadFacts? For(ReadFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (string.IsNullOrWhiteSpace(facts.Title) || string.IsNullOrWhiteSpace(facts.CreatorAsRead))
        {
            return null;
        }

        var creator = facts.CreatorAsRead.Trim();
        if (!TrimmedSpans.WorthSearching(creator.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
        {
            return null;
        }

        var titleWords = WordsOf(facts.Title).ToHashSet(StringComparer.Ordinal);
        var creatorWords = WordsOf(creator);
        if (creatorWords.Length == 0 || creatorWords.All(titleWords.Contains))
        {
            return null;
        }

        return ReadSanitizer.Sanitize(facts with { Title = creator, Creator = facts.Title.Trim(), CreatorAsRead = null });
    }

    private static string[] WordsOf(string text) =>
        TitleNormalizer.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
