using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// One read of a shot as <see cref="SeriesNeighbour"/> sees it: where it sat (null when the phone sent no position), whether it was
/// scored as a shelf spine with words to ask about (<see cref="Spine"/>), how it grounded, the words read, the title of its best
/// release (null when it names none) and that release's score.
/// </summary>
public readonly record struct SeriesRead(
    ReadBox? Box, bool Spine, ResolutionVerdict Verdict, string? ReadTitle, string? BestTitle, double Score = 0);

/// <summary>
/// A series word read on a neighbour. A spine that prints only a series' subtitle ("NEMESIS", "GENERATIONS", "ILVER FLAMES") finds
/// the wrong book (a novel called "Nemesis") or none, while the spine beside it names the series ("Star Trek: First Contact",
/// "A Court of Wings and Ruin"). The series is what a neighbour's best release carries: the text before the colon of its title, or
/// the leading words (two or more) that the best titles of the spines on both sides share. The read is then asked once more as
/// "&lt;series&gt; &lt;words read&gt;" (<see cref="IdentifyGrounding.RetrySeriesAsync"/>). This is the pure half: it needs every
/// read of the shot grounded first, because the neighbours' answers are its input, so it runs after the per-read retries, once
/// per shot, and the live <c>/identify</c> and the eval dry run both run it.
/// </summary>
/// <remarks>
/// <para>
/// A read may ask when it was scored as a spine, has a box, has words enough to search (<see cref="MinLetters"/>), and found nothing
/// or only an ambiguous answer whose best release carries no series (and does not already name the series without a colon). A
/// Matched read is never asked again. The neighbours are the nearest read on each side by the centre of its box, on the same row
/// (the boxes overlap vertically by at least half the shorter one), and not in the read's own place (the other engine's read of the
/// same spine is not a neighbour). Where several reads share the neighbour's place, the one that grounds best speaks for it.
/// Only the adjacent spines count: a series two spines away is no evidence about this one. A read with no box has no neighbours, so
/// it never asks, and a neighbour with no box is nobody's neighbour.
/// </para>
/// <para>
/// With a series on both sides the two must agree (the same words before the colon, or leading words in common); with a series
/// on one side only, that one is used. A read that already says the series is not asked with it twice. At most one series
/// is named for a read, and so one ask.
/// </para>
/// </remarks>
public static class SeriesNeighbour
{
    /// <summary>The trace step a read asked again with its neighbour's series carries (and the step of one whose ask ran out of time).</summary>
    public const string StepName = "series";

    /// <summary>A read needs at least this many letters to be worth asking with a series in front.</summary>
    public const int MinLetters = 4;

    /// <summary>A series name has at least this many letters ("Up" is one).</summary>
    public const int MinSeriesLetters = 2;

    /// <summary>Leading words two neighbours share are a series from this many words.</summary>
    public const int MinSharedWords = 2;

    /// <summary>The overlap, as a share of the shorter of two boxes' heights, that puts two reads on one row.</summary>
    public const double SameRowShare = 0.5;

    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "and", "or", "in", "on", "at", "to", "for", "with", "by", "from", "is", "my", "your", "i",
    };

    /// <summary>
    /// A read as the rule sees it. <paramref name="bestTitle"/> is the title of the best release of the answer (the caller
    /// resolves a catalogue release to its title), null when the answer names none or the caller does not know it.
    /// </summary>
    internal static SeriesRead Of(ReadFacts facts, ScoringProfileKind profile, ReadBox? box, BarcodeResolution answer, string? bestTitle) => new(
        box,
        profile == ScoringProfileKind.ShelfSpine && facts.ProductCode is null && facts.SearchText is not null,
        answer.Verdict,
        facts.SearchText,
        answer.Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous ? bestTitle : null,
        answer.Options.Count > 0 ? answer.Options[0].Score : 0);

    /// <summary>
    /// Whether the read may ask at all, whatever its neighbours say: a spine with a box and enough words that found nothing or
    /// only an ambiguous answer. A cheap test, made before anything is looked up.
    /// </summary>
    internal static bool MayAsk(SeriesRead read) =>
        read is { Spine: true, Box: not null, Verdict: ResolutionVerdict.NotFound or ResolutionVerdict.Ambiguous }
        && Letters(read.ReadTitle) >= MinLetters;

    /// <summary>
    /// The series to ask each read with, by position: null for a read that may not ask, has no neighbour carrying a series, or
    /// already says it. Pure.
    /// </summary>
    public static IReadOnlyList<string?> SeriesFor(IReadOnlyList<SeriesRead> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);

        var series = new string?[reads.Count];
        for (var i = 0; i < reads.Count; i++)
        {
            if (MayAsk(reads[i]) && SeriesOf(reads[i].BestTitle) is null)
            {
                series[i] = NamedBy(reads, i);
            }
        }

        return series;
    }

    /// <summary>The series a title carries: the text before its first colon, trimmed. Null for a title with none, or with too little before it to be a name.</summary>
    public static string? SeriesOf(string? title)
    {
        var colon = title?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        if (colon <= 0)
        {
            return null;
        }

        var head = title![..colon].Trim();
        return head.Count(char.IsLetter) >= MinSeriesLetters ? head : null;
    }

    /// <summary>The words to look for: the series, then the words as read.</summary>
    public static string AskTitle(string series, string readTitle) => $"{series.Trim()} {readTitle.Trim()}";

    /// <summary>
    /// Whether a release title names what was asked for: the series as whole words, and the words as read anywhere in it (a
    /// misread first or last letter is still inside the title: "ILVER FLAMES" is inside "A Court of Silver Flames"). Case,
    /// accents and punctuation do not matter.
    /// </summary>
    public static bool Names(string? releaseTitle, string series, string readTitle)
    {
        var title = Key(releaseTitle);
        var seriesKey = Key(series);
        var readKey = Key(readTitle);
        return title.Length > 0 && seriesKey.Length > 0 && readKey.Length > 0
            && $" {title} ".Contains($" {seriesKey} ", StringComparison.Ordinal)
            && title.Contains(readKey, StringComparison.Ordinal);
    }

    /// <summary>The titles of the releases (by id) the catalogue holds, for the reads whose best release is one of them. Uses the database: one call at a time.</summary>
    internal static async Task<IReadOnlyDictionary<Guid, string>> LocalTitlesAsync(
        IAppDbContext db, IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (releaseIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var rows = await (from release in db.Releases.AsNoTracking()
                          join work in db.Works.AsNoTracking() on release.WorkId equals work.Id
                          where releaseIds.Contains(release.Id)
                          select new { release.Id, work.Title }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Id, r => r.Title);
    }

    /// <summary>The series the neighbours of the read at <paramref name="index"/> name, or null.</summary>
    private static string? NamedBy(IReadOnlyList<SeriesRead> reads, int index)
    {
        var left = Neighbour(reads, index, -1);
        var right = Neighbour(reads, index, 1);
        var onLeft = SeriesOf(left);
        var onRight = SeriesOf(right);

        string? series;
        if (onLeft is not null && onRight is not null)
        {
            series = Key(onLeft) == Key(onRight) ? onLeft : SharedLeadingWords(left!, right!);
        }
        else if (onLeft is not null || onRight is not null)
        {
            series = onLeft ?? onRight;
        }
        else
        {
            series = left is not null && right is not null ? SharedLeadingWords(left, right) : null;
        }

        // A read that already says the series (or whose best release does) needs nothing put in front of it.
        return series is not null && !ContainsWords(Key(reads[index].ReadTitle), Key(series)) && !ContainsWords(Key(reads[index].BestTitle), Key(series))
            ? series
            : null;
    }

    /// <summary>
    /// The best release title of the spine next to the read on the given side (-1 left, 1 right): the nearest read by the centre of
    /// its box, on the read's row and not in its place; of the reads in that spine's place, the one that grounds best. Null when
    /// there is no such spine, or none of the reads in its place names a release.
    /// </summary>
    private static string? Neighbour(IReadOnlyList<SeriesRead> reads, int index, int side)
    {
        var box = reads[index].Box!.Value;
        var centre = box.X + (box.Width / 2);
        var nearest = -1;
        var gap = double.MaxValue;
        for (var j = 0; j < reads.Count; j++)
        {
            if (j == index || reads[j].Box is not { } other || box.SamePlaceAs(other) || !SameRow(box, other))
            {
                continue;
            }

            var apart = (other.X + (other.Width / 2) - centre) * side;
            if (apart > 0 && apart < gap)
            {
                (nearest, gap) = (j, apart);
            }
        }

        if (nearest < 0)
        {
            return null;
        }

        var place = reads[nearest].Box!.Value;
        var speaking = -1;
        for (var j = 0; j < reads.Count; j++)
        {
            if (j != index && reads[j].BestTitle is not null && reads[j].Box is { } other && place.SamePlaceAs(other) && !box.SamePlaceAs(other)
                && (speaking < 0 || Strength(reads[j]) > Strength(reads[speaking])))
            {
                speaking = j;
            }
        }

        return speaking < 0 ? null : reads[speaking].BestTitle;
    }

    private static double Strength(SeriesRead read) => (read.Verdict == ResolutionVerdict.Matched ? 1 : 0) + read.Score;

    private static bool SameRow(ReadBox one, ReadBox other)
    {
        var shared = Math.Min(one.Y + one.Height, other.Y + other.Height) - Math.Max(one.Y, other.Y);
        return shared > 0 && shared >= SameRowShare * Math.Min(one.Height, other.Height);
    }

    /// <summary>
    /// The leading words two titles share, as the first title wrote them: at least <see cref="MinSharedWords"/> of them, not all of
    /// either title (two copies of one title share a title, not a series), and not only small words ("In the"). Null otherwise.
    /// </summary>
    private static string? SharedLeadingWords(string first, string second)
    {
        var one = Words(first);
        var two = Words(second);
        var shared = 0;
        while (shared < one.Count && shared < two.Count && one[shared].Key == two[shared].Key)
        {
            shared++;
        }

        if (shared < MinSharedWords || shared >= one.Count || shared >= two.Count || one.Take(shared).All(w => SmallWords.Contains(w.Key)))
        {
            return null;
        }

        return WithoutTrailingPunctuation(string.Join(' ', one.Take(shared).Select(w => w.Original)));
    }

    /// <summary>The words of a title as written, each with its key: lower case, accents stripped, letters and digits only. A word with none is left out.</summary>
    private static List<(string Original, string Key)> Words(string title) =>
    [
        .. title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => (Original: word, Key: WordKey(word)))
            .Where(w => w.Key.Length > 0),
    ];

    private static string WordKey(string word) =>
        string.Concat(TitleNormalizer.StripAccents(word).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>A comparison key for words: each word's own key, joined with single spaces. Empty for null.</summary>
    private static string Key(string? text) => text is null ? string.Empty : string.Join(' ', Words(text).Select(w => w.Key));

    private static bool ContainsWords(string text, string words) =>
        words.Length > 0 && $" {text} ".Contains($" {words} ", StringComparison.Ordinal);

    private static int Letters(string? text) => text?.Count(char.IsLetter) ?? 0;

    private static string WithoutTrailingPunctuation(string text)
    {
        var end = text.Length;
        while (end > 0 && !char.IsLetterOrDigit(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }
}
