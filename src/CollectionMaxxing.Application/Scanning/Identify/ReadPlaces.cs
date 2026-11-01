namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>How well a read grounds: the ladder a place is settled by.</summary>
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
/// One read as a place settles it: where it sat in the picture (null when the phone sent no position), how well it grounds, the
/// score of its best release, and whether it is a read only the picture model made (<c>Unconfirmed</c>, the flag the phone sends
/// for a shelf read it adds beside the Vision pipeline's). A place is contested only between a read only the model made and a
/// read the other engine made: one engine's own two reads of a place are not two engines disagreeing about a spine, and a copy
/// an engine read twice is <see cref="RepeatReads"/>'s business. Nothing of the read's title or release is needed to settle a
/// place, so <c>Book</c> (the release its best option points at, in whatever form the caller already has it; null when the
/// caller has none or does not ask) is only ever used by <see cref="ReadPlaces.Confirmed"/>, never by <see cref="ReadPlaces.Weaker"/>.
/// </summary>
public readonly record struct PlacedRead(ReadBox? Box, ReadGrounding Grounding, double Score, bool Unconfirmed = false, string? Book = null);

/// <summary>
/// One item per place. Two engines read one shelf shot and both read many of the same spines: a place on the shelf holds one
/// thing, whatever either read made of it, so the read that grounds better is the row shown and the other is skipped. Better
/// means: Matched beats Ambiguous, which beats a read that found nothing; then the higher best score; then the earlier read,
/// so the Vision pipeline's own reading stands wherever the picture model read no better. Before this rule the Vision read of a
/// place always stood, and a right answer the model had read was thrown away whenever Vision had read the author's name or a
/// sticker there.
/// <para>
/// Only the two engines' reads of a place contest it (<see cref="PlacedRead.Unconfirmed"/> says which read is the model's
/// own): one engine's two reads of a place are not two engines disagreeing about a spine, and an engine that read one spine
/// twice is <see cref="RepeatReads"/>'s business, which settles two reads of the <i>same</i> release — a copy a person may
/// still want to add by hand.
/// </para>
/// <para>
/// A read whose box holds two or more of the other engine's reads that are not one place themselves is several spines merged
/// into one piece (the Vision pipeline sometimes makes one piece of a run of spines). It is no place of its own: the reads
/// inside it are the spines, so the piece loses its place and each of them keeps its own.
/// </para>
/// <para>
/// A read with no box has no place, so it neither takes one nor loses one; two reads of one place that both found nothing are
/// left alone, because there is no better read to pick. Pure.
/// </para>
/// <para>
/// When the read that stands at a place and the other engine's read it beat there (<see cref="PlacedRead.Book"/>, when the
/// caller gives it) name the same release, the two engines agreeing is itself the confirmation <c>Unconfirmed</c> exists to
/// stand in for, so the row that stands is no longer model-only (<see cref="Confirmed"/>). A different release at that place
/// is still the contest above, untouched.
/// </para>
/// </summary>
public static class ReadPlaces
{
    /// <summary>The trace step a read skipped for losing its place carries, so its row says why.</summary>
    public const string StepName = "place";

    /// <summary>
    /// One read as a place settles it, from the answer the live scorer gave it: a verdict still pending grounds nothing yet, so
    /// such a read keeps its place and takes none.
    /// </summary>
    public static PlacedRead Of(BarcodeResolution answer, ReadBox? box, bool unconfirmed) =>
        new(box, GroundingOf(answer.Verdict), answer.Options.Count > 0 ? answer.Options[0].Score : 0, unconfirmed);

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

            // Every read of the place it contests, not just the first: one engine sometimes has two reads standing in one place
            // (they are a repeat of its own, not a contest), and the other engine's read takes the place only by beating both,
            // so one spine is left with one row either way.
            var held = standing.Where(other => Contests(reads[i], reads[other])).ToList();
            if (held.Count == 0)
            {
                standing.Add(i);
            }
            else if (held.All(other => Stronger(reads[i], reads[other])))
            {
                skipped.UnionWith(held);
                standing.RemoveAll(held.Contains);
                standing.Add(i);
            }
            else
            {
                skipped.Add(i);
            }
        }

        return [.. skipped];
    }

    /// <summary>
    /// The positions of a read that stands against a same-place read the other engine made of the same release
    /// (<see cref="PlacedRead.Book"/>, neither null nor empty): the two engines agreeing there is itself the confirmation
    /// <see cref="PlacedRead.Unconfirmed"/> exists to stand in for, so a model read that stands against one is no longer
    /// model-only. A different release at the same place is still <see cref="Weaker"/>'s contest, untouched. A piece covering
    /// several spines never becomes a contest for any one of them (<see cref="Weaker"/>), so it never confirms one either: the
    /// person sees it unconfirmed, as any of its spines would be with nothing else there. Pure.
    /// </summary>
    public static IReadOnlyList<int> Confirmed(IReadOnlyList<PlacedRead> reads)
    {
        var merged = new HashSet<int>();
        for (var i = 0; i < reads.Count; i++)
        {
            if (Contends(reads[i]) && IsMergedPiece(reads, i))
            {
                merged.Add(i);
            }
        }

        var skipped = Weaker(reads).ToHashSet();
        var confirmed = new SortedSet<int>();
        for (var i = 0; i < reads.Count; i++)
        {
            if (skipped.Contains(i) || string.IsNullOrEmpty(reads[i].Book))
            {
                continue;
            }

            for (var j = 0; j < reads.Count; j++)
            {
                if (j != i && !merged.Contains(j) && OtherEngine(reads[i], reads[j]) && SamePlace(reads[i], reads[j]) && reads[j].Book == reads[i].Book)
                {
                    confirmed.Add(i);
                    break;
                }
            }
        }

        return [.. confirmed];
    }

    private static ReadGrounding GroundingOf(ResolutionVerdict verdict) => verdict switch
    {
        ResolutionVerdict.Matched => ReadGrounding.Matched,
        ResolutionVerdict.Ambiguous => ReadGrounding.Ambiguous,
        ResolutionVerdict.Pending => ReadGrounding.Unsettled,
        _ => ReadGrounding.Nothing,
    };

    /// <summary>A read settles a place only when the phone said where it sat and what it grounds is known.</summary>
    private static bool Contends(PlacedRead read) => read.Box is not null && read.Grounding != ReadGrounding.Unsettled;

    /// <summary>
    /// Whether the read at <paramref name="index"/> is several spines merged into one piece: two or more of the other engine's
    /// reads sit inside it (each smaller than it, and in its place, so at least half of each is covered by it) in places apart
    /// from each other. They are the spines, not other reads of one spine. A read no bigger than its neighbours is no piece over
    /// them, however they are staggered: a run of spines each overlapping the next is reads of a shelf, not one merged piece.
    /// </summary>
    private static bool IsMergedPiece(IReadOnlyList<PlacedRead> reads, int index)
    {
        var inside = new List<int>();
        for (var i = 0; i < reads.Count; i++)
        {
            if (i != index && Contends(reads[i]) && OtherEngine(reads[index], reads[i])
                && SamePlace(reads[index], reads[i]) && Area(reads[i]) < Area(reads[index]))
            {
                inside.Add(i);
            }
        }

        return inside.Any(j => inside.Any(k => !SamePlace(reads[j], reads[k])));
    }

    /// <summary>How much of the picture a read covers; 0 when it has no box.</summary>
    private static double Area(PlacedRead read) => read.Box is { } box ? box.Width * box.Height : 0;

    /// <summary>Whether the two reads are the two engines' reads, not one engine's own two reads of a picture.</summary>
    private static bool OtherEngine(PlacedRead first, PlacedRead second) => first.Unconfirmed != second.Unconfirmed;

    /// <summary>Whether two reads that both have a box sit in one place (<see cref="ReadBox.SamePlaceAs"/>).</summary>
    private static bool SamePlace(PlacedRead first, PlacedRead second) =>
        first.Box is { } a && second.Box is { } b && a.SamePlaceAs(b);

    /// <summary>
    /// Whether two reads contest one place. Two reads that both found nothing do not: neither grounds, so there is no better
    /// read to pick and each keeps its row.
    /// </summary>
    private static bool Contests(PlacedRead candidate, PlacedRead held) =>
        OtherEngine(candidate, held) && SamePlace(candidate, held)
        && (candidate.Grounding > ReadGrounding.Nothing || held.Grounding > ReadGrounding.Nothing);

    /// <summary>Whether <paramref name="later"/> beats <paramref name="held"/>, which came first: only a clearly better read takes the place.</summary>
    private static bool Stronger(PlacedRead later, PlacedRead held) =>
        later.Grounding != held.Grounding ? later.Grounding > held.Grounding : later.Score > held.Score;
}
