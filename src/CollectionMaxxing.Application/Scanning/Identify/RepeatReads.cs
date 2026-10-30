namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// One book in one place, one candidate per photo. An engine sometimes reads the same spine twice in one picture (once clean,
/// once with OCR junk on the end) and both reads end at the same release; left alone that is two candidates for one book, a
/// duplicate add waiting to happen. When two reads' best option is the same release <b>and they sit in the same place</b>, the
/// strongest read stands (Matched beats Ambiguous, then the higher best score, then the earlier read) and the other is a repeat.
/// Same place means the reads' boxes overlap substantially (<see cref="ReadBox.SamePlaceAs"/>); a read with no box (an older
/// phone, or a read the phone gave no position) could be anywhere, so it is compared with every other read of the release, as
/// every read once was. Reads of one release in different places are different items (five tapes whose spines all read the same
/// title) and each stands. The live <c>/identify</c> path and the eval dry run both ask this one rule, so the scorer in the eval
/// sees what a person scanning would have seen. A read with no answer (not found, or still pending) is never a repeat and never
/// makes one, and reads whose best options differ stay apart even when they share a runner-up. Copies are real, but a person
/// reviews every scan and adds a copy by hand: a read that sits in the same place as a stronger read of its release is skipped,
/// never added by itself, and the person can still un-skip it for a second copy.
/// </summary>
public static class RepeatReads
{
    /// <summary>The trace step a skipped repeat carries, so its row says why it was skipped.</summary>
    public const string StepName = "repeat";

    /// <summary>
    /// The positions (ascending) of the answers that repeat a stronger read of the same release in the same place. Pure.
    /// <paramref name="boxes"/> is each answer's box, by position; a missing list, a short one or a null entry is a read with no box.
    /// </summary>
    public static IReadOnlyList<int> Of(IReadOnlyList<BarcodeResolution> answers, IReadOnlyList<ReadBox?>? boxes = null)
    {
        // Per release, the reads standing now: more than one only when they are in different places.
        var standing = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var repeats = new SortedSet<int>();
        for (var i = 0; i < answers.Count; i++)
        {
            if (BookOf(answers[i]) is not { } book)
            {
                continue;
            }

            var stands = standing.TryGetValue(book, out var held) ? held : standing[book] = [];
            var place = stands.FindIndex(other => ReadBox.SamePlace(BoxAt(boxes, i), BoxAt(boxes, other)));
            if (place < 0)
            {
                stands.Add(i);
            }
            else if (Stronger(answers[i], answers[stands[place]]))
            {
                repeats.Add(stands[place]);
                stands[place] = i;
            }
            else
            {
                repeats.Add(i);
            }
        }

        return [.. repeats];
    }

    private static ReadBox? BoxAt(IReadOnlyList<ReadBox?>? boxes, int index) => boxes is not null && index < boxes.Count ? boxes[index] : null;

    /// <summary>The release a read's best option points at: the catalogue's release id, else the provider and its own id. Null when the read has no such answer.</summary>
    private static string? BookOf(BarcodeResolution answer)
    {
        if (answer.Verdict is not (ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous) || answer.Options.Count == 0)
        {
            return null;
        }

        var best = answer.Options[0]; // best first
        return best.LocalReleaseId is { } id ? $"release:{id:N}"
            : best.Provider is { } provider ? $"provider:{provider.Provider}:{provider.ExternalId}"
            : null;
    }

    /// <summary>Whether <paramref name="later"/> beats <paramref name="held"/>, which came first: only a clearly stronger read takes the place.</summary>
    private static bool Stronger(BarcodeResolution later, BarcodeResolution held)
    {
        var laterMatched = later.Verdict == ResolutionVerdict.Matched;
        var heldMatched = held.Verdict == ResolutionVerdict.Matched;
        return laterMatched != heldMatched ? laterMatched : later.Options[0].Score > held.Options[0].Score;
    }
}
