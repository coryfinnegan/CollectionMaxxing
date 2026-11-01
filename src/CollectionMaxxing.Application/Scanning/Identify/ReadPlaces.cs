namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>How well a read grounds: the ladder a place is settled by (issue #614).</summary>
public enum ReadGrounding
{
    /// <summary>No answer yet (a provider still owes one): such a read neither loses its place nor takes one, because what it grounds is not known.</summary>
    Unsettled = -1,

    /// <summary>Nothing: the read named no release.</summary>
    Nothing = 0,

    /// <summary>Several close releases: the person picks one.</summary>
    Ambiguous = 1,

    /// <summary>One clear release.</summary>
    Matched = 2,
}

/// <summary>
/// One read as a place settles it: where it sat in the picture (null when the phone sent no position), how well it grounds and
/// the score of its best release. Nothing of the read's title or release is needed to settle a place.
/// </summary>
public readonly record struct PlacedRead(ReadBox? Box, ReadGrounding Grounding, double Score);

/// <summary>
/// One item per place. Two engines read one shelf shot and both read many of the same spines: a place on the shelf holds one
/// thing, whatever either read made of it, so the read that grounds better is the row shown and the other is skipped. Better
/// means: Matched beats Ambiguous, which beats a read that found nothing; then the higher best score; then the earlier read,
/// so the Vision pipeline's own reading stands wherever the picture model read no better. Before this rule the Vision read of a
/// place always stood, and a right answer the model had read was thrown away whenever Vision had read the author's name or a
/// sticker there (issue #614).
/// <para>
/// A read whose box holds two or more reads that are not one place themselves is several spines merged into one piece (the
/// Vision pipeline sometimes makes one piece of a run of spines). It is no place of its own: the reads inside it are the
/// spines, so the piece loses its place and each of them keeps its own.
/// </para>
/// <para>
/// A read with no box has no place, so it neither takes one nor loses one; two reads of one place that both found nothing are
/// left alone, because there is no better read to pick. Pure, and it says nothing about what a read named:
/// <see cref="RepeatReads"/> settles two reads of the <i>same</i> release, which is a copy a person may still want.
/// </para>
/// </summary>
public static class ReadPlaces
{
    /// <summary>The trace step a read skipped for losing its place carries, so its row says why.</summary>
    public const string StepName = "place";

    /// <summary>The positions (ascending) of the reads that lose their place to a read that grounds better. Pure.</summary>
    public static IReadOnlyList<int> Weaker(IReadOnlyList<PlacedRead> reads)
    {
        var skipped = new SortedSet<int>();
        for (var i = 0; i < reads.Count; i++)
        {
            if (Contends(reads[i]) && IsMergedPiece(reads, i))
            {
                skipped.Add(i);
            }
        }

        // What is left: the reads of one place contest it, and the one that grounds best stands.
        var standing = new List<int>();
        for (var i = 0; i < reads.Count; i++)
        {
            if (!Contends(reads[i]) || skipped.Contains(i))
            {
                continue;
            }

            var place = standing.FindIndex(other => Contests(reads[i], reads[other]));
            if (place < 0)
            {
                standing.Add(i);
            }
            else if (Stronger(reads[i], reads[standing[place]]))
            {
                skipped.Add(standing[place]);
                standing[place] = i;
            }
            else
            {
                skipped.Add(i);
            }
        }

        return [.. skipped];
    }

    /// <summary>A read settles a place only when the phone said where it sat and what it grounds is known.</summary>
    private static bool Contends(PlacedRead read) => read.Box is not null && read.Grounding != ReadGrounding.Unsettled;

    /// <summary>
    /// Whether the read at <paramref name="index"/> is several spines merged into one piece: at least two of the reads in its
    /// place are not one place themselves, so they are spines inside it rather than other reads of it.
    /// </summary>
    private static bool IsMergedPiece(IReadOnlyList<PlacedRead> reads, int index)
    {
        var inside = new List<int>();
        for (var i = 0; i < reads.Count; i++)
        {
            if (i != index && Contends(reads[i]) && SamePlace(reads[index], reads[i]))
            {
                inside.Add(i);
            }
        }

        return inside.Any(j => inside.Any(k => !SamePlace(reads[j], reads[k])));
    }

    /// <summary>Whether two reads that both have a box sit in one place (<see cref="ReadBox.SamePlaceAs"/>).</summary>
    private static bool SamePlace(PlacedRead first, PlacedRead second) =>
        first.Box is { } a && second.Box is { } b && a.SamePlaceAs(b);

    /// <summary>
    /// Whether two reads contest one place. Two reads that both found nothing do not: neither grounds, so there is no better
    /// read to pick and each keeps its row.
    /// </summary>
    private static bool Contests(PlacedRead candidate, PlacedRead held) =>
        SamePlace(candidate, held) && (candidate.Grounding > ReadGrounding.Nothing || held.Grounding > ReadGrounding.Nothing);

    /// <summary>Whether <paramref name="later"/> beats <paramref name="held"/>, which came first: only a clearly better read takes the place.</summary>
    private static bool Stronger(PlacedRead later, PlacedRead held) =>
        later.Grounding != held.Grounding ? later.Grounding > held.Grounding : later.Score > held.Score;
}
