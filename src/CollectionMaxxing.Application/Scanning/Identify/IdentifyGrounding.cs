using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// What the phone read from one item: text and fields only (never a picture). Every field is a claim to be checked against
/// the catalogue, not a fact; <see cref="Text"/> is whatever was legible, <see cref="Title"/> the model's guess at the title.
/// <see cref="ProductCode"/> is set only when the printed product code named exactly one game (ADR-031): that game's title and
/// platform then stand in <see cref="Title"/> and <see cref="Platform"/>, and <see cref="ReadAs"/> keeps the words the read had
/// before (for the trace and for the card's "read as" line). Both are null on every other read.
/// <see cref="KindContext"/> is not part of the read: it is the kind the rest of the snap agreed on (<see cref="Identify.KindContext"/>),
/// set only on a read that names no kind, and it only orders level-scoring releases. It lives here so that it is part of the read's
/// value, which is what the eval run's memo keys on: the same words in a snap of films and in one of books are two lookups.
/// <see cref="CreatorAsRead"/> is the same kind of field: the author line as the phone sent it, before the sanitiser decided
/// whether it reads as a name (<see cref="ReadSanitizer"/> drops a line with "the" or "for" in it, which is how a real title
/// ends up in the author field and is lost). It is kept on a spine read only, for <see cref="IdentifyGrounding.RetryTrimmedAsync"/>
/// to ask again with the two swapped; null on every other read.
/// <see cref="Lines"/> are the lines of the spine's piece, newline-joined into one value so that two reads with the same lines are equal
/// (the eval run's memo keys on the read; a list would break that). It is kept on a spine read only, for the line retry
/// (<see cref="SpineLines"/>); null on every other read and on one from a phone that sent none.
/// </summary>
public sealed record ReadFacts(
    string? Title,
    string? Text,
    string? Creator,
    string? Edition,
    ItemCompleteness? Completeness,
    WorkKind? Kind,
    ReleaseFormat? Format,
    int? Year,
    string? Platform,
    string? ProductCode = null,
    string? ReadAs = null,
    WorkKind? KindContext = null,
    string? CreatorAsRead = null,
    string? Lines = null)
{
    /// <summary>The lines of the spine's piece, one at a time (<see cref="Lines"/> is them joined by newlines); none when the phone sent none.</summary>
    public IReadOnlyList<string> LineList => SpineLines.Split(Lines);

    /// <summary>The words to look for: the model's title when it gave one, else everything that was legible. Null when there is nothing to search.</summary>
    public string? SearchText => !string.IsNullOrWhiteSpace(Title) ? Title : !string.IsNullOrWhiteSpace(Text) ? Text : null;

    /// <summary>What to show as the words that were read: the legible text, else the title as read before a product code replaced it, else the title.</summary>
    public string? ShownText => Text ?? ReadAs ?? Title;

    public ReadItem ToReadItem() => new()
    {
        Title = SearchText,
        Kind = Kind,
        Format = Format,
        Platform = Platform,
        Year = Year,
        Creator = Creator,
        Edition = Edition,
    };

    public CatalogQuery? ToQuery() => SearchText is { } text
        ? new CatalogQuery { Title = text, Kind = Kind, Format = Format, Platform = Platform, Year = Year, Creator = Creator, Limit = 10 }
        : null;
}

/// <summary>
/// One read for <see cref="IdentifyGrounding.RetryTrimmedAsync"/>: the read, the profile it was scored with, the answer it got, and
/// how the caller asks the providers about a span of it. <see cref="Ask"/> is the caller's, so a span is asked within the same
/// places and time as the read itself (the live request's budget, or the eval's patient one); <see cref="Budget"/> is that time,
/// so the retry stops when it has run out. Reads of one batch may share both. <see cref="Local"/> is the catalogue's releases the
/// answer was scored against: an option holds only a release's id, and with the pool the retry can tell whether the read's best
/// release already holds the possessive name it would drop (<see cref="PossessivePrefix"/>). Without it that check is skipped.
/// </summary>
public sealed record TrimmedRetry(
    ReadFacts Facts,
    ScoringProfileKind Profile,
    BarcodeResolution Answer,
    Func<ReadFacts, Task<ProviderAnswers>> Ask,
    CancellationToken Budget,
    LocalPool? Local = null);

/// <summary>
/// One read of a shot for <see cref="IdentifyGrounding.RetrySeriesAsync"/>: the read, the profile it was scored with, where it sat
/// in the picture (null when the phone sent no position), the answer it has after its own retries, and how the caller asks the
/// providers about a changed read. <see cref="Ask"/> and <see cref="Budget"/> are the caller's, as for <see cref="TrimmedRetry"/>.
/// </summary>
public sealed record SeriesRetry(
    ReadFacts Facts,
    ScoringProfileKind Profile,
    ReadBox? Box,
    BarcodeResolution Answer,
    Func<ReadFacts, Task<ProviderAnswers>> Ask,
    CancellationToken Budget);

/// <summary>
/// One read of a shot for <see cref="IdentifyGrounding.RetryShotKindAsync"/>: the read, the profile it was scored with, the answer
/// it has after its own retries and the series round, and how the caller asks the providers about a changed read.
/// <see cref="Ask"/> and <see cref="Budget"/> are the caller's, as for <see cref="TrimmedRetry"/>. Where the read sat does not
/// matter to this pass.
/// </summary>
public sealed record ShotKindRetry(
    ReadFacts Facts,
    ScoringProfileKind Profile,
    BarcodeResolution Answer,
    Func<ReadFacts, Task<ProviderAnswers>> Ask,
    CancellationToken Budget);

/// <summary>
/// Grounds a read in the catalogue: the local catalogue first (trigram search), then the providers (whose answers are cached
/// by the provider layer), scored with the profile chosen for the item and the live <see cref="ScoringOptions"/>. This is where
/// "the model never decides a match alone" is enforced: whatever the phone claimed, the verdict comes from what the catalogue
/// and the providers actually hold, through <see cref="MatchScorer"/>'s thresholds. It needs no scan session, so the eval
/// harness can call it directly (<see cref="GroundAsync"/>).
/// </summary>
public sealed class IdentifyGrounding(TextSearchResolver search, IOptionsMonitor<ScoringOptions> scoring)
{
    private static readonly TraceStep NoText = new("reader", "no_text", 0);

    /// <summary>The weights and thresholds in force right now (they can be tuned while the server runs).</summary>
    public ScoringProfile ProfileFor(ScoringProfileKind kind) => scoring.CurrentValue.Profile(kind);

    /// <summary>The database step: what the local catalogue holds for the read. Run one read at a time.</summary>
    public Task<LocalPool> FindLocalAsync(ReadFacts facts, CancellationToken cancellationToken) =>
        facts.SearchText is { } text ? search.FindLocalAsync(text, cancellationToken) : Task.FromResult(LocalPool.Empty);

    /// <summary>The database step for several reads at once, in order: the same pool for each as <see cref="FindLocalAsync"/> gives, from a few queries. Run one batch at a time.</summary>
    public async Task<IReadOnlyList<LocalPool>> FindLocalManyAsync(IReadOnlyList<ReadFacts> reads, CancellationToken cancellationToken)
    {
        var texts = reads.Select(r => r.SearchText).OfType<string>().ToList();
        var pools = texts.Count == 0 ? [] : await search.FindLocalManyAsync(texts, cancellationToken);
        var next = 0;
        return [.. reads.Select(r => r.SearchText is null ? LocalPool.Empty : pools[next++])];
    }

    /// <summary>The provider step: touches no database, so many reads can run at once within one budget.</summary>
    public async Task<ProviderAnswers> AskProvidersAsync(ReadFacts facts, CancellationToken callerToken, CancellationToken budgetToken) =>
        facts.ToQuery() is { } query ? await search.AskProvidersAsync(query, callerToken, budgetToken) : ProviderAnswers.None;

    /// <summary>
    /// The verdict. A read with nothing legible is NotFound. A read that found nothing good while a provider was down or slow
    /// is Pending, so it is asked again later instead of being called unknown.
    /// </summary>
    public BarcodeResolution Score(ReadFacts facts, ScoringProfileKind profile, LocalPool local, ProviderAnswers providers)
    {
        if (facts.SearchText is null)
        {
            return new BarcodeResolution(ResolutionVerdict.NotFound, [], null, [NoText]);
        }

        var resolution = TextSearchResolver.Score(
            facts.ToReadItem(), ProfileFor(profile), local, providers, pendingWhenProvidersFailed: true, kindContext: facts.KindContext);
        return resolution with
        {
            // A provider film or game names no format: the one the cover was read as is the release's (a DVD, a VHS tape).
            Options = [.. resolution.Options.Select(o => o.Provider is { } p ? o with { Provider = p.WithReadFormat(facts.Format) } : o)],
            Steps = [.. CodeStep(facts), .. resolution.Steps, new TraceStep("profile", profile.ToString(), 0)],
        };
    }

    /// <summary>
    /// The trace line for a read whose product code decided its title (ADR-031): the code, the game it named and the words the
    /// read had before. Nothing for any other read. The step name is <c>product_code</c>, for whoever reads a candidate's trace.
    /// </summary>
    private static IEnumerable<TraceStep> CodeStep(ReadFacts facts)
    {
        if (facts.ProductCode is not { } code)
        {
            yield break;
        }

        var game = facts.Platform is { } platform ? $"{facts.Title} ({platform})" : facts.Title;
        yield return new TraceStep("product_code", facts.ReadAs is { } readAs ? $"{code} = {game}; read as: {readAs}" : $"{code} = {game}", 0);
    }

    /// <summary>
    /// The whole grounding for one read, one step after the other, within <paramref name="budget"/> for the providers. A spine that
    /// found nothing is then asked again without the stray words at its ends, without its sequel number, and last with its creator
    /// as the title; a cover that found nothing, without its sequel number; a cover or spine whose title starts with a possessive
    /// name, found nothing or not, without that name (<see cref="RetryTrimmedAsync"/>), within the same budget.
    /// </summary>
    public async Task<BarcodeResolution> GroundAsync(ReadFacts facts, ScoringProfileKind profile, TimeSpan budget, CancellationToken cancellationToken)
    {
        if (facts.SearchText is null)
        {
            return Score(facts, profile, LocalPool.Empty, ProviderAnswers.None);
        }

        var local = await FindLocalAsync(facts, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);
        var providers = await AskProvidersAsync(facts, cancellationToken, timeout.Token);
        var answer = Score(facts, profile, local, providers);
        if (!WantsRetry(facts, profile, answer, local))
        {
            return answer;
        }

        // The read is asked again in the ways AttemptsFor lists, within the budget it has.
        var retried = await RetryTrimmedAsync(
            [new TrimmedRetry(facts, profile, answer, spanFacts => AskProvidersAsync(spanFacts, cancellationToken, timeout.Token), timeout.Token, local)],
            cancellationToken);
        return retried[0];
    }

    /// <summary>
    /// Whether <see cref="RetryTrimmedAsync"/> may help a read: it was scored as a spine on a shelf or as a cover, and there is
    /// something to ask (<see cref="AttemptsFor(ReadFacts, ScoringProfileKind, BarcodeResolution)"/>). A read that found nothing
    /// may be asked several ways: for a spine, a title with words enough to drop some (<see cref="TrimmedSpans"/>), a sequel number to
    /// drop (<see cref="SequelNumber"/>) or a creator as read worth asking as the title (<see cref="CreatorSwap"/>); for a cover, a
    /// sequel number; for either, a possessive name in front of the title (<see cref="PossessivePrefix"/>). A read that found a
    /// release it is not sure of (Ambiguous) is asked only the last, and only when its best release does not already hold the
    /// possessive name (<paramref name="local"/> gives the titles of the catalogue's releases): "Schindler's List" found as
    /// "Schindler's List" has no tagline in it, while a cover's name and tagline are the commonest reason that it found the wrong
    /// one. A Pending answer, where a provider could not answer, is asked again later instead, and a
    /// Matched one is never touched. Never a read whose title a printed product code decided (ADR-031): that title is the game
    /// table's, not words Vision ran stray print into.
    /// </summary>
    public static bool WantsRetry(ReadFacts facts, ScoringProfileKind profile, BarcodeResolution answer, LocalPool? local = null) =>
        AttemptsFor(facts, profile, answer, local).Count > 0;

    /// <summary>One more way to ask about a read: the facts to ask with, and the trace step that says what they are.</summary>
    /// <summary>
    /// One more way to ask about a read: the facts to ask with, and the trace step that says what they are. <paramref name="Keeps"/> is set for
    /// a line of the spine (<see cref="SpineLines"/>) only: what such an ask finds is kept just when it says yes to the answer and the catalogue
    /// pool it was scored against (an author of the best release is printed on the spine); otherwise the ask counts as having found nothing.
    /// </summary>
    private sealed record Attempt(ReadFacts Facts, TraceStep Step, Func<BarcodeResolution, LocalPool, bool>? Keeps = null);

    /// <summary>
    /// The ways to ask again about a read, in the order they are tried. A read that found nothing: its title without its possessive
    /// name, then for a spine its trimmed spans, then its title without its sequel number, then, last, for a spine its creator as the
    /// title (the stray words at the ends of a spine and the author line of a spine are no cover's). A read that found a release
    /// it is not sure of: its title without its possessive name, and nothing else, unless its best release already holds that name.
    /// None for any other profile, a read that matched or is Pending, one already answered by one of these, or one that a product
    /// code named.
    /// </summary>
    private static List<Attempt> AttemptsFor(ReadFacts facts, ScoringProfileKind profile, BarcodeResolution answer, LocalPool? local)
    {
        var spine = profile == ScoringProfileKind.ShelfSpine;
        var unsure = answer.Verdict == ResolutionVerdict.Ambiguous && !answer.FoundByTrimmedSpan;
        if ((!spine && profile != ScoringProfileKind.Cover) || (answer.Verdict != ResolutionVerdict.NotFound && !unsure) || facts.ProductCode is not null)
        {
            return [];
        }

        var attempts = new List<Attempt>();
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The title without its possessive name goes first: it is the most pointed guess, and the only one for a read that found something.
        if (PossessivePrefix.For(facts.SearchText) is { } possessive && !NamedByBest(answer, local, possessive) && asked.Add(possessive.Title))
        {
            attempts.Add(new Attempt(facts with { Title = possessive.Title }, new TraceStep("possessive", $"{possessive.Title} (dropped: {possessive.Dropped})", 0)));
        }

        if (unsure)
        {
            // An author's name whose best release is titled with that name is asked once more with its creator as the title (a
            // fragment otherwise: FragmentReads); what that finds is offered, and nothing found leaves the answer as it was.
            if (spine && facts.Kind is null && NamesItsOwnCreator(facts, answer, local))
            {
                if (CreatorSwap.For(facts) is { } swappedName)
                {
                    attempts.Add(new Attempt(swappedName, new TraceStep("swapped", $"{swappedName.Title} (creator as read; title read: {facts.Title})", 0)));
                }

                // The read would be skipped as an author's name: another line of its spine may be the title, if the author is printed there.
                AddLineAttempts(attempts, facts, BestCreators(answer, local));
            }

            AddAnyKindAttempt(attempts, facts, spine);
            return attempts;
        }

        if (spine)
        {
            foreach (var span in TrimmedSpans.For(facts.SearchText))
            {
                if (asked.Add(span.Text))
                {
                    attempts.Add(new Attempt(facts with { Title = span.Text }, new TraceStep("trimmed", $"{span.Text} (dropped: {span.Dropped})", 0)));
                }
            }
        }

        // The title without its sequel number: not again when a span has already asked exactly that.
        if (SequelNumber.For(facts.SearchText) is { } numberless && asked.Add(numberless.Title))
        {
            attempts.Add(new Attempt(facts with { Title = numberless.Title }, new TraceStep("numeral", $"{numberless.Title} (dropped: {numberless.Token})", 0)));
        }

        if (spine && CreatorSwap.For(facts) is { } swapped)
        {
            attempts.Add(new Attempt(swapped, new TraceStep("swapped", $"{swapped.Title} (creator as read; title read: {facts.Title})", 0)));
        }

        // Last, a spine that found nothing after all of that is asked with other lines of its spine (UnfoundSpines would skip it).
        if (spine)
        {
            AddLineAttempts(attempts, facts, []);
        }

        AddAnyKindAttempt(attempts, facts, spine);
        return attempts;
    }

    /// <summary>
    /// Last, a spine read as a game is asked once more as any kind, with no platform or format: on a shelf of films the picture model
    /// can call a film a game, and a read that names a kind asks only that kind's providers. What it finds goes through
    /// <see cref="OfferedByAttempt"/>, so the read's own best release stays on top unless the find scores higher.
    /// </summary>
    private static void AddAnyKindAttempt(List<Attempt> attempts, ReadFacts facts, bool spine)
    {
        if (spine && facts.Kind == WorkKind.Game)
        {
            attempts.Add(new Attempt(facts with { Kind = null, Platform = null, Format = null }, new TraceStep("anykind", $"{facts.SearchText} (kind read: game)", 0)));
        }
    }

    /// <summary>
    /// A line's answer scored again (after every line has been asked and none kept) with the author its spine prints as the read's creator, when the answers hold a release by an author
    /// printed on the spine's other lines (<see cref="SpineLines.PrintedCreator"/>). Kept only when the new best is then kept like any line's find and
    /// its title is close to the line (at least <see cref="TextSearchResolver.CloseTitle"/>): an author's other book is no find of this line. Null otherwise.
    /// </summary>
    private BarcodeResolution? ScoredByPrintedAuthor(LineMiss miss)
    {
        var releases = miss.Pool.Items.Select(item => ((string?)item.Scoring.Title, item.Scoring.Creators))
            .Concat(miss.Provided.Found.Select(release => ((string?)release.Title, release.Creators)));
        if (SpineLines.PrintedCreator(miss.Asked, miss.Asked.Title ?? string.Empty, releases) is not { } creator)
        {
            return null;
        }

        var again = Score(miss.Asked with { Creator = creator }, miss.Profile, miss.Pool, miss.Provided);
        return again.Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous
            && again.Options.Count > 0
            && again.Options[0].Terms.FirstOrDefault(term => term.Name == "title") is { } title && title.Value >= TextSearchResolver.CloseTitle
            && miss.Attempt.Keeps!(again, miss.Pool)
                ? again
                : null;
    }

    /// <summary>A line attempt that was answered and not kept as scored: what it was asked with and what came back, to be scored again once every line has been asked.</summary>
    private sealed record LineMiss(Attempt Attempt, ReadFacts Asked, LocalPool Pool, ProviderAnswers Provided, ScoringProfileKind Profile);

    /// <summary>Whether the find scores below the read's own best release, which <see cref="OfferedByAttempt"/> then leaves on top.</summary>
    private static bool LeavesOriginalOnTop(BarcodeResolution original, BarcodeResolution found) =>
        original.Options.Count > 0 && found.Options.Count > 0 && found.Options[0].Score < original.Options[0].Score;

    /// <summary>The line attempts of <see cref="SpineLines"/>, after the attempts already listed (whose titles are not asked again).</summary>
    private static void AddLineAttempts(List<Attempt> attempts, ReadFacts facts, IReadOnlyList<string> creators)
    {
        foreach (var line in SpineLines.ToAsk(facts, attempts.Select(a => a.Facts.SearchText ?? string.Empty), creators))
        {
            attempts.Add(new Attempt(
                facts with { Title = line },
                new TraceStep(SpineLines.StepName, $"{line} (title read: {facts.SearchText})", 0),
                (found, pool) => KeepsLine(facts, line, found, pool)));
        }
    }

    /// <summary>Whether a release a line found is kept: a creator of its best release is printed on the spine (<see cref="SpineLines.KeepsFind"/>).</summary>
    private static bool KeepsLine(ReadFacts facts, string line, BarcodeResolution found, LocalPool pool)
    {
        if (found.Options.Count == 0)
        {
            return false;
        }

        var best = found.Options[0]; // best first
        return SpineLines.KeepsFind(facts, line, TitleOf(best, pool), CreatorsOf(best, pool));
    }

    /// <summary>The creators of an answer's best release, when it has one; none otherwise.</summary>
    private static IReadOnlyList<string> BestCreators(BarcodeResolution answer, LocalPool? local) =>
        answer.Options.Count == 0 ? [] : CreatorsOf(answer.Options[0], local ?? LocalPool.Empty);

    private static IReadOnlyList<string> CreatorsOf(ResolutionOption option, LocalPool pool) =>
        option.LocalReleaseId is { } id
            ? pool.Items.Where(item => item.Option.LocalReleaseId == id).Select(item => item.Scoring.Creators).FirstOrDefault() ?? []
            : option.Provider?.Creators ?? [];

    /// <summary>Whether the read's best release is titled with the read's own title, and that title is one of the release's creators (<see cref="FragmentReads.NamesItsOwnCreator"/>).</summary>
    private static bool NamesItsOwnCreator(ReadFacts facts, BarcodeResolution answer, LocalPool? local)
    {
        if (answer.Options.Count == 0)
        {
            return false;
        }

        var best = answer.Options[0]; // best first
        var pool = local ?? LocalPool.Empty;
        var creators = best.LocalReleaseId is { } id
            ? pool.Items.Where(item => item.Option.LocalReleaseId == id).Select(item => item.Scoring.Creators).FirstOrDefault() ?? []
            : best.Provider?.Creators ?? [];
        return FragmentReads.NamesItsOwnCreator(facts.SearchText, TitleOf(best, pool), creators);
    }

    /// <summary>
    /// Whether the read's best release already holds the possessive name its title starts with, compared as titles are (accents,
    /// case and punctuation aside) and as whole words: "Bram Stoker's Dracula" holds "Bram Stoker's", so the possessive is the real
    /// title and the title without it ("Dracula") is a different work. "Walt Disney Masterpieces" does not hold "Walt Disney's".
    /// A read with no options, or a best release whose title is not known, holds nothing.
    /// </summary>
    private static bool NamedByBest(BarcodeResolution answer, LocalPool? local, PossessiveDrop possessive)
    {
        if (answer.Options.Count == 0 || TitleOf(answer.Options[0], local ?? LocalPool.Empty) is not { } title)
        {
            return false;
        }

        var name = TitleNormalizer.Normalize(possessive.Name);
        return name.Length > 0 && $" {TitleNormalizer.Normalize(title)} ".Contains($" {name} ", StringComparison.Ordinal);
    }

    /// <summary>
    /// A cover or spine read whose title starts with a possessive name (<see cref="PossessivePrefix"/>: "WALT DISNEY'S MASTERPIECE
    /// PINOCCHIO") is asked again without it, whether it found nothing or only a release it is not sure of: the cover's tagline
    /// read into the title is the commonest reason a cover finds the wrong release, and the wrong release can score as Ambiguous.
    /// A spine read that found nothing is then asked again without the stray words at its ends (<see cref="TrimmedSpans"/>): Vision reads
    /// an author's name or OCR junk into the title, and the whole string then matches nothing while the rest of it matches a book.
    /// When no span finds a release it is asked without its sequel number (<see cref="SequelNumber"/>): a wrong number (II for IV,
    /// 2 for 3) is the commonest misread of a spine or a cover, and a number the catalogue does not hold finds nothing at all. A cover
    /// that found nothing is asked that, and only that, besides its possessive name. Last, a spine is asked once more with its creator
    /// as the title and its title as the creator (<see cref="CreatorSwap"/>): the phone sometimes sends the author line as the title
    /// and the real title as the creator. The answers come back in the order of <paramref name="reads"/>, each the one it had unless a
    /// possessive-less title, a span, the numberless title or the swap found a release (or the budget cut the retry short, which makes
    /// a read that found nothing Pending).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spans are tried round by round, for all the reads still looking at once: one catalogue call for the round (the database
    /// step takes one batch at a time), then the providers for every read of the round together, through each read's own
    /// <see cref="TrimmedRetry.Ask"/>. A span is searched and scored as the read's title, with the read's other facts unchanged
    /// (<see cref="ReadFacts.Text"/> stays what was read). A read stops at the first span whose grounding names a release at or
    /// above the Ambiguous threshold; at most <see cref="TrimmedSpans.MaxSpans"/> spans are asked for a read, and none once its
    /// <see cref="TrimmedRetry.Budget"/> has run out. The numberless title, then the swap, are one more attempt each after the read's
    /// last span, in a round of their own (the first, for a read with no span), asked and scored the same way; the numberless title
    /// is left out when a span has asked exactly those words.
    /// </para>
    /// <para>
    /// A retry that tried every attempt and found nothing leaves the read's answer as it was (the same answer). A retry the
    /// budget cut short does not, for a read that found nothing: if an attempt was left untried because the time ran out, or an
    /// attempt's providers could not answer (they failed, or the budget cancelled the call), the read is <b>Pending</b>, not a
    /// final NotFound. A live <c>/identify</c> request has little time, and its Pending row is finished by the background job
    /// (<c>ResolvePendingIdentify</c> runs <see cref="GroundAsync"/> again with the job's much larger budget), so <c>/identify</c>
    /// ends up with the answer the eval, with its patient budget, measures. The Pending answer's trace ends with a <c>trimmed</c>
    /// step, <c>cut_short</c>. A read that had found a release it is not sure of keeps that answer when the retry is cut short, for
    /// good: the job only takes Pending rows, so nothing asks that read again.
    /// </para>
    /// <para>
    /// The database step takes one call at a time. Reads that retry together in one batch share it; a caller that runs several
    /// batches at once (the eval grounder, one batch per read) gives <paramref name="databaseGate"/> so their catalogue calls queue.
    /// </para>
    /// <para>
    /// What a span, the numberless title, the swap or the possessive-less title finds is offered, never decided: the verdict is
    /// capped at Ambiguous and flagged (<see cref="BarcodeResolution.FoundByTrimmedSpan"/>), so the person picks and such a find can
    /// neither be added by itself, nor become a title nobody read, nor confirm another engine's read. The trace says which span found
    /// it and which words it dropped (a <c>trimmed</c> step), which sequel number was dropped (a <c>numeral</c> step), which possessive
    /// name was dropped (a <c>possessive</c> step), or that the creator as read was asked as the title (a <c>swapped</c> step).
    /// The releases the read itself found stay among the options after what the find offers (<see cref="SeriesNeighbour.WithOriginals"/>:
    /// at most three, none twice, the read's best always kept), because a possessive can be the real title ("Bram Stoker's
    /// Dracula"), and the read is then right.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<BarcodeResolution>> RetryTrimmedAsync(
        IReadOnlyList<TrimmedRetry> reads, CancellationToken cancellationToken, SemaphoreSlim? databaseGate = null)
    {
        var answers = reads.Select(r => r.Answer).ToArray();
        var attempts = reads.Select(r => AttemptsFor(r.Facts, r.Profile, r.Answer, r.Local)).ToArray();
        var cutShort = new bool[reads.Count];
        var lineKept = new bool[reads.Count];
        var lineMisses = new List<LineMiss>?[reads.Count];
        var looking = Enumerable.Range(0, reads.Count).Where(i => attempts[i].Count > 0).ToList();
        var rounds = attempts.Select(a => a.Count).DefaultIfEmpty(0).Max();
        for (var round = 0; round < rounds && looking.Count > 0; round++)
        {
            // A read with an attempt left to try and no time to try it in has not finished: its NotFound is not the final word.
            var open = new List<int>(looking.Count);
            foreach (var i in looking.Where(i => round < attempts[i].Count))
            {
                if (reads[i].Budget.IsCancellationRequested)
                {
                    cutShort[i] = true;
                }
                else
                {
                    open.Add(i);
                }
            }

            looking = open;
            if (looking.Count == 0)
            {
                break;
            }

            var asked = looking.Select(i => attempts[i][round].Facts).ToList();
            var pools = await FindLocalGatedAsync(asked, databaseGate, cancellationToken);
            var provided = await Task.WhenAll(looking.Select((i, n) => reads[i].Ask(asked[n])));
            var scored = looking.Select((i, n) => Score(asked[n], reads[i].Profile, pools[n], provided[n])).ToArray();

            var next = new List<int>(looking.Count);
            for (var n = 0; n < looking.Count; n++)
            {
                var i = looking[n];
                var attempt = attempts[i][round];
                var found = scored[n];
                if (found.Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous && (attempt.Keeps?.Invoke(found, pools[n]) ?? true))
                {
                    // A line of the spine is the find itself (the read was about to be skipped), its own release among the read's at most.
                    answers[i] = attempt.Keeps is null
                        ? OfferedByAttempt(reads[i].Answer, found, attempt.Step)
                        : Offered(reads[i].Answer, found with { Options = SeriesNeighbour.WithOriginals(found.Options, reads[i].Answer.Options) }, attempt.Step);
                    lineKept[i] |= attempt.Keeps is not null;

                    // A find that leaves the read's own best on top has not answered it (an author's name found something weaker
                    // than itself): the attempts still left, the spine's lines, are asked too, and this answer stays the fallback.
                    if (attempt.Keeps is null && round + 1 < attempts[i].Count && LeavesOriginalOnTop(reads[i].Answer, found))
                    {
                        next.Add(i);
                    }
                }
                else
                {
                    // An attempt the providers could not answer (they failed, or the budget cancelled the call) was not really tried.
                    cutShort[i] |= found.Verdict == ResolutionVerdict.Pending;
                    if (attempt.Keeps is not null)
                    {
                        (lineMisses[i] ??= []).Add(new LineMiss(attempt, asked[n], pools[n], provided[n], reads[i].Profile));
                    }

                    next.Add(i);
                }
            }

            looking = next;
        }

        for (var i = 0; i < answers.Length; i++)
        {
            // Every line of the spine was asked and none was kept as scored: the one that is kept once scored with the author the spine
            // prints is the find (the same answers, no new ask), the strongest if several are. Never for a read the budget cut short:
            // the background job asks it again with every line.
            if (!lineKept[i] && !cutShort[i] && lineMisses[i] is { } misses && misses.Count == attempts[i].Count(a => a.Keeps is not null))
            {
                var best = misses
                    .Select(miss => (Miss: miss, Again: ScoredByPrintedAuthor(miss)))
                    .Where(m => m.Again is not null)
                    .OrderByDescending(m => m.Again!.Options[0].Score)
                    .FirstOrDefault();
                if (best.Again is { } again)
                {
                    answers[i] = Offered(reads[i].Answer, again with { Options = SeriesNeighbour.WithOriginals(again.Options, reads[i].Answer.Options) }, best.Miss.Attempt.Step);
                }
            }

            // Only a read that found nothing goes Pending: one that found a release to pick from keeps it.
            if (cutShort[i] && reads[i].Answer.Verdict == ResolutionVerdict.NotFound && ReferenceEquals(answers[i], reads[i].Answer))
            {
                answers[i] = BarcodeResolution.Pending([.. reads[i].Answer.Steps, new TraceStep("trimmed", "cut_short", 0)]);
            }
        }

        return answers;
    }

    /// <summary>
    /// A spine that is only a series' subtitle ("NEMESIS", "ILVER FLAMES") is asked once more with the series its neighbour carries
    /// (<see cref="SeriesNeighbour"/>): "Star Trek nEMESIS", "A Court of ILVER FLAMES". It needs the neighbours' answers, so it is
    /// a pass over a whole shot after every read has been grounded and has had its own retries (<see cref="RetryTrimmedAsync"/>), not
    /// a retry of one read. The answers come back in the order of <paramref name="reads"/>, each the one it had unless the ask named
    /// a release whose title holds both the series and the words read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a spine with a box that found nothing, or only an ambiguous answer whose best release carries no series, is asked, and
    /// only when a neighbour (the spine to its left or right, or the leading words both share) names a series. A Matched read is
    /// never asked again. A read is asked at most once, within its <see cref="SeriesRetry.Budget"/> (<see cref="SeriesNeighbour.Budget"/>,
    /// the round's own, the same wherever the pass runs): the asks are made together
    /// (one catalogue call for all of them, then the providers through each read's <see cref="SeriesRetry.Ask"/>). The neighbours
    /// are judged on the answers the reads had when the pass began, so one read's find never becomes another's series.
    /// <paramref name="localTitles"/> gives the title of a catalogue release (an answer's option only holds its id); it is called
    /// only when a read could ask and an answer's best release is in the catalogue.
    /// </para>
    /// <para>
    /// What the ask finds is offered, never decided, as for a trimmed span: only the releases whose titles name the series and the
    /// words read are kept, the verdict is capped at Ambiguous, the answer is flagged
    /// (<see cref="BarcodeResolution.FoundByTrimmedSpan"/>) and its trace says which series was put in front (a <c>series</c> step).
    /// A read that already had an ambiguous answer keeps its own options after the ones the series named
    /// (<see cref="SeriesNeighbour.WithOriginals"/>): the series find is a better guess for a subtitle read alone, not proof that the
    /// release the read found was wrong, so the person can still pick it. An ask that names nothing leaves the read's answer as it
    /// was (the same object).
    /// </para>
    /// <para>
    /// A round that runs out of time, or whose ask the providers could not answer, leaves the read's answer exactly as it was: a
    /// read that found nothing stays NotFound, never Pending, because nothing could finish it later. <see cref="ResolvePendingIdentify"/>
    /// grounds one read on its own, and a stored read has no box, so a Pending read would only come back as the same answer after a
    /// job and a spinner (and as an error, if the providers were down).
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<BarcodeResolution>> RetrySeriesAsync(
        IReadOnlyList<SeriesRetry> reads,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, string>>> localTitles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(localTitles);

        var answers = reads.Select(r => r.Answer).ToArray();
        if (!reads.Any(r => SeriesNeighbour.MayAsk(SeriesNeighbour.Of(r.Facts, r.Profile, r.Box, r.Answer, null))))
        {
            return answers;
        }

        var titles = await BestTitlesAsync([.. reads.Select(r => r.Answer)], localTitles, cancellationToken);
        var series = SeriesNeighbour.SeriesFor([.. reads.Select((r, i) => SeriesNeighbour.Of(r.Facts, r.Profile, r.Box, r.Answer, titles[i]))]);

        // A read with no time left to make its ask is left as it was.
        var open = Enumerable.Range(0, reads.Count).Where(i => series[i] is not null && !reads[i].Budget.IsCancellationRequested).ToList();
        if (open.Count == 0)
        {
            return answers;
        }

        var asked = open.Select(i => SeriesAsk(reads[i].Facts, series[i]!)).ToList();
        var pools = await FindLocalManyAsync(asked, cancellationToken);
        var scored = await Task.WhenAll(open.Select(async (i, n) => Score(asked[n], reads[i].Profile, pools[n], await reads[i].Ask(asked[n]))));
        for (var n = 0; n < open.Count; n++)
        {
            // An ask the providers could not answer (they failed, or the budget cancelled the call) is Pending, and found nothing.
            if (scored[n].Verdict is not (ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous))
            {
                continue;
            }

            var i = open[n];
            var named = scored[n].Options.Where(o => SeriesNeighbour.Names(TitleOf(o, pools[n]), series[i]!, reads[i].Facts.SearchText!)).ToList();
            if (named.Count > 0)
            {
                var why = new TraceStep(SeriesNeighbour.StepName, $"{asked[n].Title} (series: {series[i]})", 0);
                answers[i] = Offered(reads[i].Answer, scored[n] with { Options = SeriesNeighbour.WithOriginals(named, reads[i].Answer.Options) }, why);
            }
        }

        return answers;
    }

    /// <summary>The read asked with the series in front of the words read, cleaned as a read is (the length cut, the creator judged again against the new title).</summary>
    private static ReadFacts SeriesAsk(ReadFacts facts, string series) => ReadSanitizer.Sanitize(
        facts with { Title = IdentifyCleaning.Text(SeriesNeighbour.AskTitle(series, facts.SearchText!), IdentifyCleaning.MaxFieldLength) });

    /// <summary>The title of the best release of each answer that has one (null for the rest), the catalogue's releases through <paramref name="localTitles"/>.</summary>
    private static async Task<string?[]> BestTitlesAsync(
        IReadOnlyList<BarcodeResolution> answers,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, string>>> localTitles,
        CancellationToken cancellationToken)
    {
        var titles = new string?[answers.Count];
        var inCatalogue = new Dictionary<int, Guid>();
        for (var i = 0; i < answers.Count; i++)
        {
            var answer = answers[i];
            if (answer.Verdict is not (ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous) || answer.Options.Count == 0)
            {
                continue;
            }

            var best = answer.Options[0]; // best first
            if (best.LocalReleaseId is { } id)
            {
                inCatalogue[i] = id;
            }
            else
            {
                titles[i] = best.Provider?.Title;
            }
        }

        if (inCatalogue.Count > 0)
        {
            var found = await localTitles([.. inCatalogue.Values.Distinct()], cancellationToken);
            foreach (var (i, id) in inCatalogue)
            {
                titles[i] = found.TryGetValue(id, out var title) ? title : null;
            }
        }

        return titles;
    }

    /// <summary>The title of an option an ask found: the provider's own, or the catalogue's from the pool the ask was scored against.</summary>
    private static string? TitleOf(ResolutionOption option, LocalPool pool) =>
        option.LocalReleaseId is { } id
            ? pool.Items.Where(item => item.Option.LocalReleaseId == id).Select(item => item.Scoring.Title).FirstOrDefault()
            : option.Provider?.Title;

    /// <summary>
    /// A spine on a shelf that found another kind than the shelf's is asked once more as the shelf's kind (<see cref="ShotKindRule"/>):
    /// "cnoi Emergency Contact o" beside books that matched finds three films, and asked as a book it finds the book. It needs the
    /// other spines' answers (the shot's kind is the kind its Matched spines agree on, or what the shelf's own print said), so it is
    /// a pass over a whole shot after every read has been grounded and has had its own retries (<see cref="RetryTrimmedAsync"/>) and
    /// the series round (<see cref="RetrySeriesAsync"/>), not a retry of one read. The answers come back in the order of
    /// <paramref name="reads"/>, each the one it had unless the ask named a release of the shot's kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a spine that names no kind of its own, is Ambiguous, and whose best release is of a known kind outside the shot's family
    /// is asked (<see cref="ShotKindRule.Asks"/>); a Matched, not-found or pending read, and a read with its own kind, never are. The
    /// ask is the read with <see cref="ReadFacts.Kind"/> set to the shot's kind, so the providers of that kind alone are asked and
    /// the type term counts. A read is also asked with its best option's own title when that differs from the read's after
    /// <see cref="TitleNormalizer"/> (<see cref="ShotKindRule.AskTitle"/>; <paramref name="localTitles"/> gives a catalogue release's
    /// title): the fuzzy catalogue matched the garbled words to a clean title the exact one can find. The finds of both asks are scored
    /// against the read's own facts and go through the same offer. A read is asked at most twice, both within its <see cref="ShotKindRetry.Budget"/>
    /// (<see cref="ShotKindRule.Budget"/>, the pass's own, the same wherever it runs): the asks are made together (one catalogue call
    /// for all of them, then the providers through each read's <see cref="ShotKindRetry.Ask"/>). The kinds are judged on the answers
    /// the reads had when the pass began. <paramref name="alsoOfTheSnap"/> are the reads another engine sent with the same snap: they count toward the shot's kind and are never asked. <paramref name="localKinds"/> gives the kind of a catalogue release (an answer's option only
    /// holds its id); it is called only when the shot could ask at all and an answer's best release is in the catalogue.
    /// </para>
    /// <para>
    /// What the ask finds is offered, never decided, as for a trimmed span: only when the best release of the ask is of the shot's
    /// kind, only the releases of that kind are kept, the verdict is capped at Ambiguous, the answer is flagged
    /// (<see cref="BarcodeResolution.FoundByTrimmedSpan"/>) and its trace says which kind was asked (a <c>kind</c> step). The read
    /// keeps its own options (<see cref="ShotKindRule.Offer"/>): the find first only when it scores strictly better than the read's
    /// best option, else after it, and always with at least one of the read's own, so a film on a shelf of books can still be picked.
    /// The ask names the shot's kind, so what it finds earns a type term that the read's own answer never had; the finds are kept
    /// with the score they have without it (<see cref="ShotKindRule.OnOriginalFooting"/>), so a different title does not go ahead
    /// of the read's best option on the strength of its kind alone. Once the find and the read's own options are ordered, a
    /// release of the shot's kind with the same normalised title as the best option comes first whatever its score
    /// (<see cref="ShotKindRule.SameTitledOfKindFirst"/>, the 2026-11-05 decision), the other release staying in the offer.
    /// That reorder also runs when the ask finds nothing of the shot's kind, or the providers could not answer it: a kindless
    /// read on a shot with a clear kind gets its same-titled release of that kind first from the options it already has.
    /// A read that names its own kind is never reordered.
    /// </para>
    /// <para>
    /// A pass that runs out of time leaves the read's answer exactly as it was (a failed ask still allows the reorder above), and
    /// never makes a read Pending: <see cref="ResolvePendingIdentify"/> grounds one read on its own, with no neighbours, so it could
    /// only come back as the same answer after a job and a spinner.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<BarcodeResolution>> RetryShotKindAsync(
        IReadOnlyList<ShotKindRetry> reads,
        WorkKind? printKind,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, WorkKind>>> localKinds,
        CancellationToken cancellationToken,
        IReadOnlyList<ShotKindRead>? alsoOfTheSnap = null,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, string>>>? localTitles = null)
    {
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(localKinds);

        // The reads of the same snap that another engine sent (the picture model's, beside Vision's) say what the shot is of but are
        // not asked again here: they join the vote only.
        var aside = alsoOfTheSnap ?? [];
        var answers = reads.Select(r => r.Answer).ToArray();
        if (!ShotKindRule.CouldAsk([.. reads.Select(r => ShotKindRule.Of(r.Facts, r.Profile, r.Answer, null)), .. aside], printKind))
        {
            return answers;
        }

        var bestKinds = await BestKindsAsync(reads, localKinds, cancellationToken);
        var kinds = ShotKindRule.AskKinds(
            printKind, [.. reads.Select((r, i) => ShotKindRule.Of(r.Facts, r.Profile, r.Answer, bestKinds[i])), .. aside]);

        // A read with no time left to make its ask is left as it was.
        var open = Enumerable.Range(0, reads.Count).Where(i => kinds[i] is not null && !reads[i].Budget.IsCancellationRequested).ToList();
        if (open.Count == 0)
        {
            return answers;
        }

        var asked = open.Select(i => ReadSanitizer.Sanitize(reads[i].Facts with { Kind = kinds[i] })).ToList();

        // A read is also asked with its best option's own title when that differs from the read's: the fuzzy catalogue matched the
        // garbled words to a clean title, and the shot's kind's exact catalogue can be queried with it. Same round, same budget.
        var bestTitles = await BestTitlesAsync(
            [.. answers.Select((answer, i) => kinds[i] is null ? NoAnswer : answer)], localTitles ?? NoLocalTitles, cancellationToken);
        var byTitle = new List<(int N, string Title, ReadFacts Facts)>();
        for (var n = 0; n < open.Count; n++)
        {
            if (ShotKindRule.AskTitle(reads[open[n]].Facts.SearchText ?? string.Empty, bestTitles[open[n]]) is { } title)
            {
                byTitle.Add((n, title, ReadSanitizer.Sanitize(reads[open[n]].Facts with { Kind = kinds[open[n]], Title = IdentifyCleaning.Text(title, IdentifyCleaning.MaxFieldLength) })));
            }
        }

        var pools = await FindLocalManyAsync([.. asked, .. byTitle.Select(b => b.Facts)], cancellationToken);

        // Every ask is scored against what the read itself said (with the shot's kind set, removed again by OnOriginalFooting), never against the title asked.
        var scoredTasks = open.Select(async (i, n) => Score(asked[n], reads[i].Profile, pools[n], await reads[i].Ask(asked[n]))).ToList();
        var scoredByTitleTasks = byTitle.Select(async (b, k) =>
            Score(asked[b.N], reads[open[b.N]].Profile, pools[open.Count + k], await reads[open[b.N]].Ask(b.Facts))).ToList();
        var scored = await Task.WhenAll(scoredTasks);
        var scoredByTitle = await Task.WhenAll(scoredByTitleTasks);
        for (var n = 0; n < open.Count; n++)
        {
            var i = open[n];
            var shotKind = kinds[i]!.Value;
            var original = reads[i].Answer;
            var why = new TraceStep(ShotKindRule.StepName, $"{asked[n].Title} (kind: {EnumText.ToText(shotKind)})", 0);
            var asks = new List<(BarcodeResolution Scored, LocalPool Pool, TraceStep Why)> { (scored[n], pools[n], why) };
            var viaTitle = byTitle.FindIndex(b => b.N == n);
            if (viaTitle >= 0)
            {
                asks.Add((scoredByTitle[viaTitle], pools[open.Count + viaTitle], new TraceStep(ShotKindRule.StepName, $"{byTitle[viaTitle].Title} (kind: {EnumText.ToText(shotKind)})", 0)));
            }

            // An ask the providers could not answer (they failed, or the budget cancelled the call) is Pending, and found nothing. And
            // the ask must have come out for the shot's kind: if its best release is another kind, the read keeps what it found.
            var came = asks.Where(a => a.Scored.Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous
                && ShotKindRule.IsOfKind(KindOf(a.Scored.Options[0], [a.Pool]), shotKind)).ToList();
            if (came.Count > 0)
            {
                var found = came
                    .SelectMany(a => a.Scored.Options.Where(o => ShotKindRule.IsOfKind(KindOf(o, [a.Pool]), shotKind)).Select(ShotKindRule.OnOriginalFooting))
                    .DistinctBy(OptionKey)
                    .OrderByDescending(o => o.Score)
                    .ToList();
                var lead = came.MaxBy(a => a.Scored.Options[0].Score); // the first of equals: the read's own title
                answers[i] = Offered(original, lead.Scored with { Options = ShotKindRule.Offer(found, original.Options) }, lead.Why);
            }

            // Either way, a release of the shot's kind with the best option's own title comes first, from the offer or the read's own.
            var allPools = asks.Select(a => a.Pool).ToList();
            var ordered = ShotKindRule.SameTitledOfKindFirst(answers[i].Options, option => DescribeOption(option, allPools), shotKind);
            if (!ReferenceEquals(ordered, answers[i].Options))
            {
                answers[i] = answers[i] with { Options = ordered, Steps = ReferenceEquals(answers[i], original) ? [.. original.Steps, why] : answers[i].Steps };
            }
        }

        return answers;
    }

    private static readonly BarcodeResolution NoAnswer = new(ResolutionVerdict.NotFound, [], null, []);

    private static readonly Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, string>>> NoLocalTitles =
        (_, _) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    /// <summary>What names a release in an offer: its catalogue id, else its provider and the provider's id.</summary>
    private static string OptionKey(ResolutionOption option) =>
        option.LocalReleaseId?.ToString() ?? $"{option.Provider?.Provider}/{option.Provider?.ExternalId}";

    /// <summary>The kind of the best release of each answer that has one (null for the rest): a provider release's own, a catalogue release's through <paramref name="localKinds"/>.</summary>
    private static async Task<WorkKind?[]> BestKindsAsync(
        IReadOnlyList<ShotKindRetry> reads,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, WorkKind>>> localKinds,
        CancellationToken cancellationToken)
    {
        var kinds = new WorkKind?[reads.Count];
        var inCatalogue = new Dictionary<int, Guid>();
        for (var i = 0; i < reads.Count; i++)
        {
            var answer = reads[i].Answer;
            if (answer.Verdict is not (ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous) || answer.Options.Count == 0)
            {
                continue;
            }

            var best = answer.Options[0]; // best first
            if (best.LocalReleaseId is { } id)
            {
                inCatalogue[i] = id;
            }
            else
            {
                kinds[i] = best.Provider?.Kind;
            }
        }

        if (inCatalogue.Count > 0)
        {
            var found = await localKinds([.. inCatalogue.Values.Distinct()], cancellationToken);
            foreach (var (i, id) in inCatalogue)
            {
                kinds[i] = found.TryGetValue(id, out var kind) ? kind : null;
            }
        }

        return kinds;
    }

    /// <summary>The title and kind of an option an ask found: the provider's own, or the catalogue's from the pool the ask was scored against.</summary>
    private static (string Title, WorkKind Kind)? DescribeOption(ResolutionOption option, IReadOnlyList<LocalPool> pools)
    {
        if (option.LocalReleaseId is { } id)
        {
            return pools.SelectMany(pool => pool.Items).Where(item => item.Option.LocalReleaseId == id).Select(item => ((string Title, WorkKind Kind)?)(item.Scoring.Title, item.Scoring.Kind)).FirstOrDefault();
        }

        return option.Provider is { } provider ? (provider.Title, provider.Kind) : null;
    }

    /// <summary>The kind of an option an ask found: the provider's own, or the catalogue's from the pool the ask was scored against.</summary>
    private static WorkKind? KindOf(ResolutionOption option, IReadOnlyList<LocalPool> pools) =>
        option.LocalReleaseId is { } id
            ? pools.SelectMany(pool => pool.Items).Where(item => item.Option.LocalReleaseId == id).Select(item => (WorkKind?)item.Scoring.Kind).FirstOrDefault()
            : option.Provider?.Kind;

    /// <summary>
    /// <see cref="FindLocalManyAsync"/>, one caller at a time when <paramref name="databaseGate"/> is given: for callers whose reads
    /// retry at the same time on one database context (a context serves one query at a time), each holding the gate only for its call.
    /// </summary>
    private async Task<IReadOnlyList<LocalPool>> FindLocalGatedAsync(
        IReadOnlyList<ReadFacts> reads, SemaphoreSlim? databaseGate, CancellationToken cancellationToken)
    {
        if (databaseGate is null)
        {
            return await FindLocalManyAsync(reads, cancellationToken);
        }

        await databaseGate.WaitAsync(cancellationToken);
        try
        {
            return await FindLocalManyAsync(reads, cancellationToken);
        }
        finally
        {
            databaseGate.Release();
        }
    }

    /// <summary>
    /// What an attempt found, as an answer for the read it was cut from. A read that found nothing has no release of its own to
    /// lose: the answer is the find (<see cref="Offered"/>). A read that found a release it is not sure of keeps it: when the find
    /// scores at least as well as the read's best, it goes first and the read's releases follow (<see cref="SeriesNeighbour.WithOriginals"/>),
    /// flagged as a find (<see cref="BarcodeResolution.FoundByTrimmedSpan"/>); when it scores worse, the read's best release stays
    /// first, the find's releases come right after it and the read's other releases after those, at most three, and the answer
    /// is still the read's own: not flagged, so it can still be a repeat of, and confirm, another engine's read of the same release.
    /// A possessive can be the real title ("Ocean's Eleven"), and then the title without it is a different, shorter work.
    /// </summary>
    private static BarcodeResolution OfferedByAttempt(BarcodeResolution original, BarcodeResolution found, TraceStep why)
    {
        if (original.Options.Count == 0 || found.Options.Count == 0 || !LeavesOriginalOnTop(original, found))
        {
            return Offered(original, found with { Options = SeriesNeighbour.WithOriginals(found.Options, original.Options) }, why);
        }

        var options = new List<ResolutionOption> { original.Options[0] };
        foreach (var option in found.Options.Concat(original.Options.Skip(1)))
        {
            if (options.Count < SeriesNeighbour.MaxOptions && !options.Any(kept => SeriesNeighbour.SameRelease(kept, option)))
            {
                options.Add(option);
            }
        }

        return original with { Options = options, Steps = [.. original.Steps, why, .. found.Steps] };
    }

    /// <summary>
    /// What a possessive-less title, a span, the numberless title, the swap or the neighbour's series found, as an answer for the
    /// read it was cut from: the find's options as given, never Matched, and a step to say why.
    /// </summary>
    private static BarcodeResolution Offered(BarcodeResolution original, BarcodeResolution found, TraceStep why) => found with
    {
        Verdict = ResolutionVerdict.Ambiguous,
        FoundByTrimmedSpan = true,
        Steps = [.. original.Steps, why, .. found.Steps],
    };
}
