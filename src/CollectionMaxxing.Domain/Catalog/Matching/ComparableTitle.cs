namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>
/// A title made ready to compare: its key (see <see cref="TitleNormalizer.Normalize"/>), its words, its years and its main
/// title are worked out once, so a title compared with many others (a read against its candidates) is normalized once
/// rather than once per comparison. Same scores as <see cref="TitleNormalizer.Similarity"/> and
/// <see cref="TitleNormalizer.TitleSimilarity"/>, which make one of these for each side. Immutable.
/// </summary>
public sealed class ComparableTitle
{
    private static readonly string[] SubtitleMarkers = [":", " - ", " – ", " — ", "("];

    private static readonly IReadOnlySet<int> NoYears = new HashSet<int>();

    private const int MinSurnameLength = 3;

    /// <summary>The fewest letters a title found inside a longer read must carry for the find to count.</summary>
    private const int MinContainedLetters = 4;

    /// <summary>The most words of other print a read may carry around a title it holds.</summary>
    private const int MostExtraWords = 3;

    /// <summary>The most digits a number standing after a title may have and still be read as a volume number.</summary>
    private const int MostVolumeDigits = 2;

    // Edit distance keeps two rows of this many numbers on the stack; a longer title gets rows of its own.
    private const int MostStackedRowLength = 256;

    /// <param name="title">Null or blank is a title with no words, which scores 0 against everything.</param>
    public ComparableTitle(string? title)
        : this(title, withMainTitle: true)
    {
    }

    private ComparableTitle(string? title, bool withMainTitle)
    {
        // The normaliser drops a genre-label subtitle; the main title is found in the same stripped title, so a release
        // labelled "Dune: A Novel" is a plain "Dune" and not a main title that has lost a subtitle.
        var named = GenreSubtitle.WithoutLabel(title);
        Key = TitleNormalizer.Normalize(named);
        if (Key.Length > 0)
        {
            Words = new WordSet(Key);
            Tokens = Key.Split(' ');
            Letters = Key.Count(char.IsLetter);
            Years = YearsIn(Key);
            var lastSpace = Key.LastIndexOf(' ');
            if (lastSpace > 0 && IsYear(Key.AsSpan(lastSpace + 1), out _))
            {
                WordsWithoutYear = new WordSet(Key[..lastSpace]);
            }
        }

        MainTitle = withMainTitle ? FindMainTitle(named) : null;
    }

    /// <summary>The comparison key: what <see cref="TitleNormalizer.Normalize"/> gives for the title.</summary>
    public string Key { get; }

    /// <summary>The words of the key; null when the title has none.</summary>
    private WordSet? Words { get; }

    /// <summary>The key's words in the order they were read, duplicates kept; empty when the title has none.</summary>
    private string[] Tokens { get; } = [];

    /// <summary>How many letters the key carries (digits, punctuation and spaces are not letters).</summary>
    private int Letters { get; }

    /// <summary>The words of the key without a year at its end, when it ends with one.</summary>
    private WordSet? WordsWithoutYear { get; }

    private IReadOnlySet<int> Years { get; } = NoYears;

    /// <summary>The part before a subtitle (a colon, a spaced dash or a bracket), or null when the title has none or nothing would be left of it.</summary>
    private ComparableTitle? MainTitle { get; }

    /// <inheritdoc cref="TitleNormalizer.Similarity"/>
    public double Similarity(ComparableTitle other) => Alike(other, wordOrderCredit: 1.0, withContainment: false);

    /// <inheritdoc cref="TitleNormalizer.TitleSimilarity"/>
    public double TitleSimilarity(ComparableTitle other) => Alike(other, TitleNormalizer.ReorderedWordsCredit, withContainment: true);

    /// <summary>
    /// True when both titles contain a four digit year (1900 to 2099) and the years are not the same: an annual series
    /// ("Madden NFL 2005" against "Madden NFL 2004"). A year that only one title has is not a conflict.
    /// </summary>
    internal bool YearsConflictWith(ComparableTitle other) =>
        Years.Count > 0 && other.Years.Count > 0 && !Years.SetEquals(other.Years);

    /// <summary>
    /// This title, read off a spine, with this creator's name taken out, when the read carries it: a spine reads title and
    /// author as one line ("DUNE FRANK HERBERT", "ODELL harbor light keepers"). The surname must be there (publishers often
    /// print it alone); then it and any other word of the name are removed, the last time each appears. Null when the
    /// surname isn't in the read, or when nothing of the title would be left (a book titled after its author keeps its
    /// words).
    /// </summary>
    internal ComparableTitle? WithoutCreator(ComparableTitle creator)
    {
        var read = new List<string>(Key.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var name = creator.Key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (read.Count < 2 || name.Length == 0 || name[^1] is not { Length: >= MinSurnameLength } surname
            || surname.All(char.IsAsciiDigit) || !read.Contains(surname))
        {
            return null;
        }

        foreach (var word in name.Distinct(StringComparer.Ordinal))
        {
            var at = read.LastIndexOf(word);
            if (at >= 0)
            {
                read.RemoveAt(at);
            }
        }

        return read.Count == 0 ? null : new ComparableTitle(string.Join(' ', read));
    }

    private double Alike(ComparableTitle other, double wordOrderCredit, bool withContainment)
    {
        var whole = WholeSimilarity(this, other, wordOrderCredit);
        if (whole >= 1.0)
        {
            return whole;
        }

        var best = whole;
        if (MainTitle is { } mainTitle)
        {
            best = Math.Max(best, TitleNormalizer.MainTitleCredit * WholeSimilarity(mainTitle, other, wordOrderCredit));
        }

        if (other.MainTitle is { } otherMainTitle)
        {
            best = Math.Max(best, TitleNormalizer.MainTitleCredit * WholeSimilarity(this, otherMainTitle, wordOrderCredit));
        }

        if (withContainment && Holding(other) is { } contained)
        {
            best = Math.Max(best, contained);
        }

        return best;
    }

    /// <summary>
    /// What this read is worth against a title it holds whole, or null when it does not hold one. A spine reads the
    /// title with other print around it: the author's first name and initial in front, a stray pair of letters, a
    /// sticker, a shelf number. The other title's words must stand together and in order inside this one, with at
    /// least one and at most <see cref="MostExtraWords"/> words of other print left over, and the title found must
    /// carry at least <see cref="MinContainedLetters"/> letters, so "Dune" inside a read is a find and "Up" is not.
    /// A volume number or a year left standing after the title belongs to the title and costs the same quarter it
    /// costs anywhere else ("harbor light 2" is not "Harbor Light"); a longer run of digits is a sticker or a shelf
    /// mark and costs nothing. The result is never 1: a title found inside other print is close, not the same, and the
    /// scorer will not add it on its own strength (see <see cref="MatchScorer"/>).
    /// </summary>
    private double? Holding(ComparableTitle other)
    {
        if (Words is null || other.Words is null || other.Letters < MinContainedLetters)
        {
            return null;
        }

        var extra = Tokens.Length - other.Tokens.Length;
        if (extra is < 1 or > MostExtraWords)
        {
            return null;
        }

        var at = RunOf(Tokens, other.Tokens);
        if (at < 0)
        {
            return null;
        }

        var credit = TitleNormalizer.ContainedTitleCredit - (TitleNormalizer.ExtraPrintCost * (extra - 1));
        for (var i = at + other.Tokens.Length; i < Tokens.Length; i++)
        {
            if (IsVolumeNumberOrYear(Tokens[i]))
            {
                return credit * TitleNormalizer.NumberMismatchCredit;
            }
        }

        return credit;
    }

    // One or two digits standing after a title is a volume ("Harbor Light 2"), and four digits in range is a year:
    // both belong to the title. Any other run of digits is a sticker, a shelf mark or a price.
    private static bool IsVolumeNumberOrYear(string word) =>
        word is { Length: > 0 } && word.All(char.IsAsciiDigit)
        && (word.Length <= MostVolumeDigits || IsYear(word, out _));

    /// <summary>Where <paramref name="run"/> stands inside <paramref name="words"/> as whole words in order, or -1.</summary>
    private static int RunOf(string[] words, string[] run)
    {
        for (var at = 0; at + run.Length <= words.Length; at++)
        {
            if (words.AsSpan(at, run.Length).SequenceEqual(run))
            {
                return at;
            }
        }

        return -1;
    }

    private static ComparableTitle? FindMainTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var cut = title.Length;
        foreach (var marker in SubtitleMarkers)
        {
            var at = title.IndexOf(marker, StringComparison.Ordinal);
            if (at > 0 && at < cut)
            {
                cut = at;
            }
        }

        if (cut == title.Length)
        {
            return null;
        }

        var main = new ComparableTitle(title[..cut], withMainTitle: false);
        return main.Key.Length == 0 ? null : main;
    }

    private static double WholeSimilarity(ComparableTitle a, ComparableTitle b, double wordOrderCredit)
    {
        if (a.Words is not { } left || b.Words is not { } right)
        {
            return 0.0;
        }

        if (a.YearsConflictWith(b))
        {
            return Core(left, right, wordOrderCredit);
        }

        var best = Core(left, right, wordOrderCredit);
        if (a.WordsWithoutYear is { } leftWithoutYear)
        {
            best = Math.Max(best, Core(leftWithoutYear, right, wordOrderCredit));
        }

        if (b.WordsWithoutYear is { } rightWithoutYear)
        {
            best = Math.Max(best, Core(left, rightWithoutYear, wordOrderCredit));
        }

        if (a.WordsWithoutYear is { } leftBare && b.WordsWithoutYear is { } rightBare)
        {
            best = Math.Max(best, Core(leftBare, rightBare, wordOrderCredit));
        }

        return best;
    }

    private static IReadOnlySet<int> YearsIn(string key)
    {
        HashSet<int>? years = null;
        foreach (var range in key.AsSpan().Split(' '))
        {
            if (IsYear(key.AsSpan()[range], out var year))
            {
                (years ??= []).Add(year);
            }
        }

        return years ?? NoYears;
    }

    private static bool IsYear(ReadOnlySpan<char> token, out int year)
    {
        year = 0;
        if (token.Length != 4)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        year = ((token[0] - '0') * 1000) + ((token[1] - '0') * 100) + ((token[2] - '0') * 10) + (token[3] - '0');
        return year is >= 1900 and <= 2099;
    }

    private static double Core(WordSet left, WordSet right, double wordOrderCredit)
    {
        if (left.Text == right.Text)
        {
            return 1.0;
        }

        var edit = 1.0 - ((double)EditDistance(left.Text, right.Text) / Math.Max(left.Text.Length, right.Text.Length));

        var shared = 0;
        foreach (var word in left.Set)
        {
            if (right.Set.Contains(word))
            {
                shared++;
            }
        }

        var union = left.Set.Count + right.Set.Count - shared;
        var overlap = union == 0 ? 0.0 : (double)shared / union;
        if (overlap >= 1.0)
        {
            // Every word is shared but the two strings are not the same (that returned above): the order differs, or a word
            // is repeated on one side. Names read in either order; a title is credited less.
            overlap = wordOrderCredit;
        }

        var score = Math.Max(edit, overlap);

        if (!left.Numbers.AsSpan().SequenceEqual(right.Numbers))
        {
            score *= TitleNormalizer.NumberMismatchCredit;
        }

        return Math.Clamp(score, 0.0, 1.0);
    }

    private static int EditDistance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var rowLength = b.Length + 1;
        Span<int> previous = rowLength <= MostStackedRowLength ? stackalloc int[rowLength] : new int[rowLength];
        Span<int> current = rowLength <= MostStackedRowLength ? stackalloc int[rowLength] : new int[rowLength];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            var finished = previous;
            previous = current;
            current = finished;
        }

        return previous[b.Length];
    }

    /// <summary>A normalized text with its distinct words, and the ones that are numbers, in order.</summary>
    private sealed class WordSet
    {
        public WordSet(string text)
        {
            Text = text;
            Set = new HashSet<string>(text.Split(' '), StringComparer.Ordinal);
            Numbers = [.. Set.Where(word => word.All(char.IsAsciiDigit)).Order(StringComparer.Ordinal)];
        }

        public string Text { get; }

        public HashSet<string> Set { get; }

        public string[] Numbers { get; }
    }
}
