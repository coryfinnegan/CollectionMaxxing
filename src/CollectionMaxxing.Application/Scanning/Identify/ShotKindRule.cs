using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// One read of a shot as <see cref="ShotKindRule"/> sees it: whether it was scored as a shelf spine with words to ask about
/// (<see cref="Spine"/>), the kind it names of its own (<see cref="OwnKind"/>: the picture model's reads do, the Vision tier's do
/// not), how it grounded, and the kind of its best release (null when it names none, or the caller does not know it).
/// </summary>
public readonly record struct ShotKindRead(bool Spine, WorkKind? OwnKind, ResolutionVerdict Verdict, WorkKind? BestKind)
{
    /// <summary>Whether the read names a kind of its own. <see cref="WorkKind.Other"/> is the reader saying it does not know.</summary>
    public bool NamesKind => OwnKind is { } kind && kind != WorkKind.Other;
}

/// <summary>
/// The kind a shot's spines found, and the spines that found another. The Vision tier reads text, not kinds, so a read such as
/// "cnoi Emergency Contact o" on a shelf of novels asks every provider and finds three films, and "SARAH J. THRONE OF GLASS" finds a
/// game (0.61) over the book (0.56). The shelf already says what it is: the spines beside it matched books. The shot's kind is that
/// kind (<see cref="KindContext.Of"/>, with the shelf's own print as the phone read it), and a kind-less spine whose answer is
/// ambiguous and whose best release is of another kind is asked once more as the shot's kind
/// (<see cref="IdentifyGrounding.RetryShotKindAsync"/>), so only that kind's providers are asked and the type term counts. This is
/// the pure half. It needs every read of the shot grounded first, because the matched spines are its input, so it runs after the
/// per-read retries and the series round, once per shot, and the live <c>/identify</c> and the eval dry run both run it.
/// </summary>
/// <remarks>
/// <para>
/// The shot's kind is <see cref="KindContext.Of"/> over what the reads say the shot is of: a read that names a kind says it, and a
/// Matched spine that names none says the kind of its best release. The print's kind wins when no read says one of its family
/// (<see cref="KindContext.PrintThatSpeaks"/>), else it is the kind at least <see cref="KindContext.MinReads"/> of them agree on at
/// <see cref="KindContext.MinShare"/>. When the reads that name a kind split with no such majority and one names the print's kind,
/// the print's kind is the shot's (<see cref="KindContext.Of"/>). So a photo whose reads name their kinds (the picture model's) gets
/// the kind the cleaning gave its kind-less reads, and the Vision tier's reads, which name none, speak through what they matched. The signal is Matched
/// on purpose: a high score is not it (the Vision reads of a shelf of tapes ground to books at 0.9 and over), while a Matched spine
/// has cleared the lead the scorer needs for a sure thing. On a shelf of films the Matched spines are none or one, so the rule
/// never says "book" there.
/// </para>
/// <para>
/// A spine is asked again when it was scored as a spine with a title (no product code), names no kind of its own, is Ambiguous,
/// and its best release is of a known kind outside the shot's family. A Matched, not-found or pending read is never asked again
/// (<see cref="Asks"/>), and neither is a read that names its own kind. What the ask finds is offered, never decided
/// (<see cref="IdentifyGrounding.RetryShotKindAsync"/>).
/// </para>
/// </remarks>
public static class ShotKindRule
{
    /// <summary>The trace step a read asked again as the shot's kind carries.</summary>
    public const string StepName = "kind";

    /// <summary>
    /// The time the pass has, whoever runs it: the asks of every read that has a kind to ask with share it, counted from the moment
    /// the pass starts. It is the pass's own, not what is left of the request's budget, and the eval grounder uses the same one, so
    /// the eval measures what a person scanning can be given. The pending job cannot make this pass (it grounds one read on its own,
    /// with no neighbours), so a pass that runs out leaves every answer as it was; nothing is made Pending for it.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    /// <summary>The most releases an answer offers: the usual three.</summary>
    public const int MaxOptions = SeriesNeighbour.MaxOptions;

    /// <summary>The name the scorer gives the term that sets the read's kind (or format) against the release's.</summary>
    private const string TypeTerm = "type";

    /// <summary>
    /// A read as the rule sees it. <paramref name="bestKind"/> is the kind of the best release of the answer (the caller resolves a
    /// catalogue release to its work's kind), null when the answer names none or the caller does not know it.
    /// </summary>
    internal static ShotKindRead Of(ReadFacts facts, ScoringProfileKind profile, BarcodeResolution answer, WorkKind? bestKind) => new(
        profile == ScoringProfileKind.ShelfSpine && facts.ProductCode is null && facts.SearchText is not null,
        facts.Kind,
        answer.Verdict,
        answer.Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous ? bestKind : null);

    /// <summary>
    /// The kind the shot is of: <see cref="KindContext.Of"/> over what each read says (<see cref="Said"/>), with the shelf's own
    /// print (<paramref name="printKind"/>, null when it said none). Null when the shot has no clear kind. Pure.
    /// </summary>
    public static WorkKind? Of(WorkKind? printKind, IReadOnlyList<ShotKindRead> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);

        return KindContext.Of(printKind, reads.Select(Said));
    }

    /// <summary>The kind a read says the shot is of: its own when it names one, else the kind of its best release when it is a Matched spine, else none.</summary>
    private static WorkKind? Said(ShotKindRead read) =>
        read.NamesKind ? read.OwnKind : read is { Spine: true, Verdict: ResolutionVerdict.Matched } ? read.BestKind : null;

    /// <summary>
    /// Whether the pass could ask anything, judged before any release is looked up: some spine is a kind-less ambiguous read, and
    /// the shot could have a kind (the shelf printed one, or at least <see cref="KindContext.MinReads"/> reads say one). A cheap
    /// test that spares the lookup of the kinds of the answers on the shots where nothing can happen.
    /// </summary>
    internal static bool CouldAsk(IReadOnlyList<ShotKindRead> reads, WorkKind? printKind) =>
        reads.Any(r => r is { Spine: true, NamesKind: false, Verdict: ResolutionVerdict.Ambiguous })
        && (printKind is { } printed && printed != WorkKind.Other
            || reads.Count(r => r.NamesKind || r is { Spine: true, Verdict: ResolutionVerdict.Matched }) >= KindContext.MinReads);

    /// <summary>The kind to ask each read again with, by position: null for a read that is not asked again. Pure.</summary>
    public static IReadOnlyList<WorkKind?> AskKinds(WorkKind? printKind, IReadOnlyList<ShotKindRead> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);

        var kinds = new WorkKind?[reads.Count];
        if (Of(printKind, reads) is not { } shotKind)
        {
            return kinds;
        }

        for (var i = 0; i < reads.Count; i++)
        {
            if (Asks(reads[i], shotKind))
            {
                kinds[i] = shotKind;
            }
        }

        return kinds;
    }

    /// <summary>
    /// Whether the read is asked again as <paramref name="shotKind"/>: a spine that names no kind of its own, is Ambiguous, and
    /// whose best release is of a known kind (not <see cref="WorkKind.Other"/>, which is the catalogue not knowing) outside the
    /// shot kind's family.
    /// </summary>
    public static bool Asks(ShotKindRead read, WorkKind shotKind) =>
        read is { Spine: true, NamesKind: false, Verdict: ResolutionVerdict.Ambiguous, BestKind: { } best }
        && best != WorkKind.Other
        && !KindContext.InFamily(best, shotKind);

    /// <summary>
    /// The title a read that is asked again is also asked with: its best option's own title (the clean title the fuzzy catalogue
    /// matched the garbled words to), so the shot's kind's exact catalogue is queried with it. Null when the best option has no title,
    /// or it is the read's own title once normalised (<see cref="TitleNormalizer.Normalize"/>), since that ask is already made. Pure.
    /// </summary>
    public static string? AskTitle(string readTitle, string? bestOptionTitle)
    {
        ArgumentNullException.ThrowIfNull(readTitle);

        if (string.IsNullOrWhiteSpace(bestOptionTitle))
        {
            return null;
        }

        var best = TitleNormalizer.Normalize(bestOptionTitle);
        return best.Length == 0 || best == TitleNormalizer.Normalize(readTitle) ? null : bestOptionTitle.Trim();
    }

    /// <summary>Whether a release of <paramref name="kind"/> (null when unknown) is of the shot's kind: the same family.</summary>
    public static bool IsOfKind(WorkKind? kind, WorkKind shotKind) => kind is { } known && KindContext.InFamily(known, shotKind);

    /// <summary>
    /// The options of an answer the ask found something for, <paramref name="found"/> being the releases of the shot's kind it
    /// found (best first, at least one), already on the footing the read's own answer had (<see cref="OnOriginalFooting"/>). Only
    /// when the best of them scores strictly better than the read's best option does the find come first and the read's own
    /// options follow (<see cref="SeriesNeighbour.WithOriginals"/>: at most <see cref="MaxOptions"/>, and the read keeps at least
    /// its best one, so the film stays pickable on a shelf of books). On a tie or worse the read's best option stays first and the
    /// finds come after it, except for a release of the shot's kind that is the same work by title (<see cref="SameTitledOfKindFirst"/>).
    /// A release is never offered twice. Pure.
    /// </summary>
    public static IReadOnlyList<ResolutionOption> Offer(IReadOnlyList<ResolutionOption> found, IReadOnlyList<ResolutionOption> original)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(original);

        if (original.Count == 0 || Math.Round(found[0].Score - original[0].Score, 9) > 0)
        {
            return SeriesNeighbour.WithOriginals(found, original);
        }

        return [original[0], .. SeriesNeighbour.WithOriginals(found, [.. original.Skip(1)]).Take(MaxOptions - 1)];
    }

    /// <summary>
    /// The decision of 2026-11-05: on a shot whose kind is clear (<see cref="KindContext.Of"/>: the print's kind
    /// when it speaks, else <see cref="KindContext.MinReads"/> reads at <see cref="KindContext.MinShare"/>, else the print over a split;
    /// with no kind there is no call here), a kind-less read's best option of another kind gives way to a
    /// release of the shot's kind that has the same normalised title (<see cref="TitleNormalizer.Normalize"/>), whatever its score.
    /// It reverses the earlier choice that a same-titled book is not a better answer than the right film of a DVD among books: the
    /// eval's wrong-kind rows (19 of the combined false positives) are the right title in the wrong kind, and the shelf's own kind is
    /// the better evidence. The other release stays in the offer, only the order changes, the verdict is untouched, and a read that
    /// names its own kind is never passed here (<see cref="Asks"/>). <paramref name="describe"/> gives an option's title and kind
    /// (null when unknown, and the option then does not move). Pure.
    /// </summary>
    public static IReadOnlyList<ResolutionOption> SameTitledOfKindFirst(
        IReadOnlyList<ResolutionOption> options, Func<ResolutionOption, (string Title, WorkKind Kind)?> describe, WorkKind shotKind)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(describe);

        if (options.Count < 2 || describe(options[0]) is not { } best || IsOfKind(best.Kind, shotKind))
        {
            return options;
        }

        var title = TitleNormalizer.Normalize(best.Title);
        if (title.Length == 0)
        {
            return options;
        }

        for (var i = 1; i < options.Count; i++)
        {
            if (describe(options[i]) is { } other && IsOfKind(other.Kind, shotKind) && TitleNormalizer.Normalize(other.Title) == title)
            {
                return [options[i], .. options.Take(i), .. options.Skip(i + 1)];
            }
        }

        return options;
    }

    /// <summary>
    /// An option the ask found, scored the way the read's own answer was: as if the ask had named no kind. The ask names the shot's
    /// kind, so what it finds earns the type term that the read's own answer never had, and a differently titled release of the shot's kind
    /// would beat the read's best option by that term alone (a same-titled one goes first on purpose, <see cref="SameTitledOfKindFirst"/>). The option's score is the scorer's own terms without the type term, weighed the
    /// way it weighs them, and its terms are those. The cap the scorer put on the option for a known conflict (a platform or a
    /// title-year mismatch) is not in the terms, but it is in the option's score, so the rescored score never exceeds that score:
    /// a capped option stays capped. That is the score everything after this sees, the order of the offer included. An option
    /// with no terms (not scored by the text search) is returned as it is.
    /// </summary>
    public static ResolutionOption OnOriginalFooting(ResolutionOption option)
    {
        ArgumentNullException.ThrowIfNull(option);

        var terms = option.Terms.Where(term => term.Name != TypeTerm).ToList();
        var weight = terms.Sum(term => term.Weight);
        return option.Terms.Count == 0 || weight <= 0
            ? option
            : option with { Score = Math.Min(Math.Round(terms.Sum(term => term.Weight * term.Value) / weight, 9), option.Score), Terms = terms };
    }

    /// <summary>The kinds of the works of the releases (by id) the catalogue holds, for the reads whose best release is one of them. Uses the database: one call at a time.</summary>
    internal static async Task<IReadOnlyDictionary<Guid, WorkKind>> LocalKindsAsync(
        IAppDbContext db, IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (releaseIds.Count == 0)
        {
            return new Dictionary<Guid, WorkKind>();
        }

        var rows = await (from release in db.Releases.AsNoTracking()
                          join work in db.Works.AsNoTracking() on release.WorkId equals work.Id
                          where releaseIds.Contains(release.Id)
                          select new { release.Id, work.Kind }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Id, r => r.Kind);
    }
}
