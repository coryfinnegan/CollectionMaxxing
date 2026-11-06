using System.Text.RegularExpressions;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// One spine read as the fragment rule judges it: the title it was asked as, everything legible on its piece (what other pieces'
/// publisher marks are looked for in), whether it is a spine that names no kind of its own, how it grounded, and the title and
/// creators of its best release (null and empty when it has none), and the titles of every option it was answered with.
/// </summary>
public readonly record struct FragmentRead(
    string? Title, string? Printed, bool KindlessSpine, ResolutionVerdict Verdict, string? BestTitle, IReadOnlyList<string> BestCreators,
    IReadOnlyList<string>? OptionTitles = null);

/// <summary>
/// A spine read that only found itself. The Vision pipeline reads a piece of a shelf photo, and sometimes the piece holds no
/// title at all: a number, a format or banner line ("VHS", "Family Entertainment", "Director's Cut"), a publisher's mark, or only the
/// author's name. The catalogue still answers such a read (an author's name is the title of some biography), and the answer is
/// a row nobody shelved. Like a repeat (<see cref="RepeatReads"/>) it is skipped, never deleted: the row stays with its answer and the
/// person can still un-skip it. Pure; the live <c>/identify</c> path and the eval dry run both ask this one rule.
/// <para>
/// Only an Ambiguous, kindless spine read is judged (a read that found nothing is a different question, and a read that names its
/// kind has said what it is). It is a fragment when either holds:
/// </para>
/// <list type="number">
/// <item>Its title is only print that is never a title: digits only, a format or banner phrase (<see cref="BannerPhrases"/>), or
/// a publisher's mark (<see cref="PublisherMarks"/>) that <b>another piece of the same snap also prints</b>, so a real one-word
/// title that is also a studio's name is kept unless a second spine prints it.</item>
/// <item>Its best release is named by the read's own title (<see cref="NamedBy"/>), and that title is a person's name: the release's
/// own creator, or words that are all in one creator name another answer of the snap carries (one edit allowed for words of five
/// letters or more, the way OCR garbles a name). Before such a read is given up on, its creator is asked as the title
/// (<see cref="IdentifyGrounding.RetryTrimmedAsync"/> and <see cref="CreatorSwap"/>); what that finds is no longer named by the read.</item>
/// </list>
/// </summary>
public static partial class FragmentReads
{
    /// <summary>The trace step a skipped fragment carries, so its row says why it was skipped.</summary>
    public const string StepName = "fragment";

    /// <summary>
    /// Format and banner phrases printed on a spine or box that are never a title. Matched as whole phrases: a title is a banner
    /// only when nothing is left of it once these (and ordinals such as "25th") are taken out. Lower case, apostrophes dropped,
    /// punctuation as a space.
    /// </summary>
    internal static readonly string[] BannerPhrases =
    [
        "vhs", "dvd", "blu ray", "bluray", "home video", "family entertainment", "widescreen", "full screen", "fullscreen",
        "directors cut", "special edition", "anniversary edition", "collectors edition", "special collectors edition",
    ];

    /// <summary>
    /// Publishers' and studios' marks, matched as whole words and only when another piece of the snap also prints them. Kept to
    /// marks that are not ordinary words on their own: words like "network" or "universal" are real titles and are not here
    /// ("universal pictures" is). A mark may be followed by <see cref="MarkSuffixes"/> ("Penguin Classics").
    /// </summary>
    internal static readonly string[] PublisherMarks =
    [
        "penguin", "bantam", "harpercollins", "harper collins", "random house", "scholastic", "del rey", "berkley", "pocket books",
        "simon schuster", "warner bros", "walt disney", "touchstone", "columbia tristar", "dreamworks", "lionsgate", "miramax",
        "mgm", "paramount pictures", "universal pictures", "twentieth century fox", "20th century fox",
    ];

    /// <summary>The words that may follow a publisher's mark without making it something else.</summary>
    internal static readonly string[] MarkSuffixes =
        ["books", "publishing", "publishers", "press", "classics", "pictures", "studios", "home", "video", "entertainment"];

    /// <summary>How many characters one title may be longer than the other for the two to name the same release.</summary>
    private const int PrefixSlack = 6;

    /// <summary>The shortest word that is forgiven one misread letter when it is compared with a creator's name.</summary>
    private const int MinLettersForOneEdit = 5;

    /// <summary>The positions (ascending) of the reads that are fragments. Pure.</summary>
    public static IReadOnlyList<int> Of(IReadOnlyList<FragmentRead> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);

        var fragments = new List<int>();
        for (var i = 0; i < reads.Count; i++)
        {
            var read = reads[i];
            if (!read.KindlessSpine || read.Verdict != ResolutionVerdict.Ambiguous || string.IsNullOrWhiteSpace(read.Title))
            {
                continue;
            }

            if (NeverATitle(read) || IsMarkOfOtherPiece(reads, i) || IsPersonsName(reads, i))
            {
                fragments.Add(i);
            }
        }

        return fragments;
    }

    /// <summary>
    /// Whether a read's best release is named by the read's own title and that title is the release's own creator: the one case
    /// a single read can tell, so the one the swap (<see cref="CreatorSwap"/>) is attempted for while the read is still being grounded.
    /// </summary>
    public static bool NamesItsOwnCreator(string? title, string? bestTitle, IReadOnlyList<string> bestCreators) =>
        NamedBy(title, bestTitle) && bestCreators.Any(creator => SameWords(creator, title));

    /// <summary>
    /// One read in the form the rule judges. <paramref name="releaseOf"/> gives the title and creators of a catalogue release by id
    /// (null when it is unknown); a provider's release carries its own.
    /// </summary>
    public static FragmentRead ReadOf(
        ReadFacts facts, ScoringProfileKind profile, BarcodeResolution answer,
        Func<Guid, (string Title, IReadOnlyList<string> Creators)?> releaseOf)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(releaseOf);

        string? bestTitle = null;
        IReadOnlyList<string> bestCreators = [];
        var optionTitles = new List<string>();
        if (answer.Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous && answer.Options.Count > 0)
        {
            var best = answer.Options[0]; // best first
            if (best.LocalReleaseId is { } id)
            {
                if (releaseOf(id) is { } known)
                {
                    (bestTitle, bestCreators) = known;
                }
            }
            else if (best.Provider is { } provider)
            {
                (bestTitle, bestCreators) = (provider.Title, provider.Creators);
            }

            foreach (var option in answer.Options)
            {
                if (option.LocalReleaseId is { } optionId)
                {
                    if (releaseOf(optionId) is { } known)
                    {
                        optionTitles.Add(known.Title);
                    }
                }
                else if (option.Provider is { } optionProvider)
                {
                    optionTitles.Add(optionProvider.Title);
                }
            }
        }

        return new FragmentRead(
            facts.SearchText, facts.Text ?? facts.Title, profile == ScoringProfileKind.ShelfSpine && facts.Kind is null, answer.Verdict, bestTitle, bestCreators, optionTitles);
    }

    /// <summary>The catalogue release ids the rule needs the title and creators of: every option of each answer, when it is in the catalogue.</summary>
    public static IReadOnlyCollection<Guid> ReleaseIdsOf(IEnumerable<BarcodeResolution> answers) =>
    [
        .. answers
            .Where(a => a.Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous && a.Options.Count > 0)
            .SelectMany(a => a.Options)
            .Select(o => o.LocalReleaseId)
            .OfType<Guid>()
            .Distinct(),
    ];

    /// <summary>The title and creators of the catalogue releases of <paramref name="releaseIds"/>.</summary>
    internal static async Task<IReadOnlyDictionary<Guid, (string Title, IReadOnlyList<string> Creators)>> LocalReleasesAsync(
        IAppDbContext db, IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (releaseIds.Count == 0)
        {
            return new Dictionary<Guid, (string, IReadOnlyList<string>)>();
        }

        var rows = await (from release in db.Releases.AsNoTracking()
                          join work in db.Works.AsNoTracking() on release.WorkId equals work.Id
                          where releaseIds.Contains(release.Id)
                          select new { release.Id, work.Title, work.CreatorsJson }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Id, r => (r.Title, ItemProjection.Creators(r.CreatorsJson)));
    }

    /// <summary>
    /// Whether a line of a spine's piece is print that is never a title: a banner or format phrase, a publisher's or studio's mark (on
    /// its own, or with a suffix such as "Books"), or digits only. The line retry (<see cref="SpineLines"/>) never asks such a line.
    /// </summary>
    public static bool IsNeverATitle(string? line)
    {
        var words = PlainWords(line);
        if (words.Length == 0)
        {
            return true;
        }

        var plain = string.Join(' ', words);
        return words.All(word => word.All(char.IsDigit)) || IsBanner(plain) || PublisherMarks.Any(mark => IsMark(plain, mark));
    }

    // ---- case 1: print that is never a title ----------------------------------------------------------------------------------------

    private static bool NeverATitle(FragmentRead read)
    {
        var words = PlainWords(read.Title);
        if (words.Length == 0)
        {
            return false;
        }

        // Digits are a fragment only when no release among the read's options is titled with them: a real numeric title is kept.
        var digitsOnly = words.All(word => word.All(char.IsDigit)) && !(read.OptionTitles ?? []).Any(title => SameWords(title, read.Title));
        return digitsOnly || IsBanner(string.Join(' ', words));
    }

    private static bool IsBanner(string plain)
    {
        if (plain.Contains("bestselling author", StringComparison.Ordinal) || plain.Contains("best selling author", StringComparison.Ordinal))
        {
            return true;
        }

        var rest = $" {Ordinal().Replace(plain, " ")} ";
        var removed = false;
        foreach (var phrase in BannerPhrases.OrderByDescending(p => p.Length))
        {
            var padded = $" {phrase} ";
            while (rest.Contains(padded, StringComparison.Ordinal))
            {
                rest = rest.Replace(padded, " ", StringComparison.Ordinal);
                removed = true;
            }
        }

        return removed && rest.Trim().Length == 0;
    }

    /// <summary>Whether the title is a publisher's mark that some other piece of the snap also prints.</summary>
    private static bool IsMarkOfOtherPiece(IReadOnlyList<FragmentRead> reads, int index)
    {
        var words = PlainWords(reads[index].Title);
        var mark = PublisherMarks.FirstOrDefault(m => IsMark(string.Join(' ', words), m));
        if (mark is null)
        {
            return false;
        }

        var padded = $" {mark} ";
        return reads.Where((_, j) => j != index).Any(other => $" {string.Join(' ', PlainWords(other.Printed))} ".Contains(padded, StringComparison.Ordinal));
    }

    private static bool IsMark(string plainTitle, string mark)
    {
        if (plainTitle == mark)
        {
            return true;
        }

        if (!plainTitle.StartsWith(mark + " ", StringComparison.Ordinal))
        {
            return false;
        }

        return plainTitle[(mark.Length + 1)..].Split(' ').All(word => MarkSuffixes.Contains(word, StringComparer.Ordinal));
    }

    // ---- case 2: an author's name, answered by a release titled with it --------------------------------------------------------------

    private static bool IsPersonsName(IReadOnlyList<FragmentRead> reads, int index)
    {
        var read = reads[index];
        if (!NamedBy(read.Title, read.BestTitle))
        {
            return false;
        }

        if (read.BestCreators.Any(creator => SameWords(creator, read.Title)))
        {
            return true;
        }

        var words = NormalWords(read.Title);
        return words.Length > 0 && reads
            .Where((other, j) => j != index && other.Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous)
            .SelectMany(other => other.BestCreators)
            .Any(creator => words.All(word => NormalWords(creator).Any(name => SameName(word, name))));
    }

    /// <summary>Whether the read's title names the release's title: the same normalised title, or one a prefix of the other within <see cref="PrefixSlack"/> characters.</summary>
    private static bool NamedBy(string? title, string? bestTitle)
    {
        var read = TitleNormalizer.Normalize(title);
        var best = TitleNormalizer.Normalize(bestTitle);
        if (read.Length == 0 || best.Length == 0)
        {
            return false;
        }

        return read == best
            || (Math.Abs(read.Length - best.Length) <= PrefixSlack
                && (best.StartsWith(read, StringComparison.Ordinal) || read.StartsWith(best, StringComparison.Ordinal)));
    }

    private static bool SameWords(string? a, string? b) => TitleNormalizer.Normalize(a) is { Length: > 0 } x && x == TitleNormalizer.Normalize(b);

    private static bool SameName(string word, string name) =>
        word == name || (word.Length >= MinLettersForOneEdit && name.Length >= MinLettersForOneEdit && OneEditApart(word, name));

    private static bool OneEditApart(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        var i = 0;
        while (i < shorter.Length && shorter[i] == longer[i])
        {
            i++;
        }

        if (i == shorter.Length)
        {
            return true; // equal, or one letter longer
        }

        return shorter.Length == longer.Length
            ? shorter[(i + 1)..] == longer[(i + 1)..] // one letter replaced
            : shorter[i..] == longer[(i + 1)..]; // one letter inserted
    }

    private static string[] NormalWords(string? text) =>
        TitleNormalizer.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Lower-case words of letters and digits, apostrophes dropped ("Director's" is "directors"), everything else a break.</summary>
    private static string[] PlainWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. NonWord().Split(text.ToLowerInvariant().Replace("'", string.Empty, StringComparison.Ordinal).Replace("’", string.Empty, StringComparison.Ordinal))
                .Where(word => word.Length > 0)];

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\b\d+(st|nd|rd|th)\b")]
    private static partial Regex Ordinal();
}
