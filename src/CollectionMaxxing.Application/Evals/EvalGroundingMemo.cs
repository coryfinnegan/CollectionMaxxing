using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// What grounding answered for each distinct read in one eval run. The same title is usually read by all three engines and by
/// several photos, and every lookup costs catalogue and provider calls (a 19-photo run made 636 of them), so a read that
/// repeats is answered from here. A read is "the same" when its cleaned <see cref="ReadFacts"/> (a record of plain fields, so it
/// compares by value) and its <see cref="ScoringProfileKind"/> are equal: the profile follows from the photo too, not only the read.
/// </summary>
/// <remarks>
/// Made by the handler at the start of a run and passed down, so it lives exactly as long as that run and the next run asks the
/// catalogue afresh. Not thread-safe: a run grounds one read after the other.
/// </remarks>
public sealed class EvalGroundingMemo
{
    private readonly Dictionary<(ReadFacts Facts, ScoringProfileKind Profile), BarcodeResolution> _answers = [];

    /// <summary>How many distinct reads were really grounded.</summary>
    public int Grounded { get; private set; }

    /// <summary>How many times a read was answered from what an earlier read had already found.</summary>
    public int Reused { get; private set; }

    /// <summary>
    /// The answer for the read: the remembered one, else <paramref name="ground"/>'s, which is then remembered. A grounding that
    /// throws leaves nothing behind.
    /// </summary>
    public async Task<BarcodeResolution> GroundAsync(ReadFacts facts, ScoringProfileKind profile, Func<Task<BarcodeResolution>> ground)
    {
        var key = (facts, profile);
        if (_answers.TryGetValue(key, out var known))
        {
            Reused++;
            return known;
        }

        var answer = await ground();
        _answers[key] = answer;
        Grounded++;
        return answer;
    }
}
