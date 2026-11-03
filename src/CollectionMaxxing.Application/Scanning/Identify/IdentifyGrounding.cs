using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
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
    string? CreatorAsRead = null)
{
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
/// so the retry stops when it has run out. Reads of one batch may share both.
/// </summary>
public sealed record TrimmedRetry(
    ReadFacts Facts,
    ScoringProfileKind Profile,
    BarcodeResolution Answer,
    Func<ReadFacts, Task<ProviderAnswers>> Ask,
    CancellationToken Budget);

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
    /// as the title; a cover that found nothing, without its sequel number (<see cref="RetryTrimmedAsync"/>), within the same budget.
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
        if (!WantsRetry(facts, profile, answer))
        {
            return answer;
        }

        // A spine that found nothing is asked again without the stray words at its ends, without its sequel number, then with its creator as the title, and a cover without its sequel number, within the budget it has.
        var retried = await RetryTrimmedAsync(
            [new TrimmedRetry(facts, profile, answer, spanFacts => AskProvidersAsync(spanFacts, cancellationToken, timeout.Token), timeout.Token)],
            cancellationToken);
        return retried[0];
    }

    /// <summary>
    /// Whether <see cref="RetryTrimmedAsync"/> may help a read: it was scored as a spine on a shelf or as a cover, nothing was found
    /// for it (a Pending answer, where a provider could not answer, is asked again later instead, and an Ambiguous or Matched one
    /// has found its release), and there is something to ask (<see cref="AttemptsFor(ReadFacts, ScoringProfileKind, BarcodeResolution)"/>):
    /// for a spine, a title with words enough to drop some (<see cref="TrimmedSpans"/>), a sequel number to drop
    /// (<see cref="SequelNumber"/>) or a creator as read worth asking as the title (<see cref="CreatorSwap"/>); for a cover, a sequel
    /// number. Never a read whose title a printed product code decided (ADR-031): that title is the game table's, not words Vision
    /// ran stray print into.
    /// </summary>
    public static bool WantsRetry(ReadFacts facts, ScoringProfileKind profile, BarcodeResolution answer) =>
        AttemptsFor(facts, profile, answer).Count > 0;

    /// <summary>One more way to ask about a read that found nothing: the facts to ask with, and the trace step that says what they are.</summary>
    private sealed record Attempt(ReadFacts Facts, TraceStep Step);

    /// <summary>
    /// The ways to ask again about a read, in the order they are tried. A spine: its trimmed spans, then its title without its sequel
    /// number, then, last, its creator as the title. A cover: its title without its sequel number, and nothing else (the stray words
    /// at the ends of a spine and the author line of a spine are no cover's). None for any other profile, or a read that found
    /// something, or that a product code named.
    /// </summary>
    private static List<Attempt> AttemptsFor(ReadFacts facts, ScoringProfileKind profile, BarcodeResolution answer)
    {
        var spine = profile == ScoringProfileKind.ShelfSpine;
        if ((!spine && profile != ScoringProfileKind.Cover) || answer.Verdict != ResolutionVerdict.NotFound || facts.ProductCode is not null)
        {
            return [];
        }

        var attempts = new List<Attempt>();
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (spine)
        {
            foreach (var span in TrimmedSpans.For(facts.SearchText))
            {
                asked.Add(span.Text);
                attempts.Add(new Attempt(facts with { Title = span.Text }, new TraceStep("trimmed", $"{span.Text} (dropped: {span.Dropped})", 0)));
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

        return attempts;
    }

    /// <summary>
    /// A spine read that found nothing is asked again without the stray words at its ends (<see cref="TrimmedSpans"/>): Vision reads
    /// an author's name or OCR junk into the title, and the whole string then matches nothing while the rest of it matches a book.
    /// When no span finds a release it is asked without its sequel number (<see cref="SequelNumber"/>): a wrong number (II for IV,
    /// 2 for 3) is the commonest misread of a spine or a cover, and a number the catalogue does not hold finds nothing at all. A cover
    /// that found nothing is asked that, and only that. Last, a spine is asked once more with its creator as the title and its title
    /// as the creator (<see cref="CreatorSwap"/>): the phone sometimes sends the author line as the title and the real title as the
    /// creator. The answers come back in the order of <paramref name="reads"/>, each the one it had unless a span, the numberless
    /// title or the swap found a release (or the budget cut the retry short, which makes it Pending).
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
    /// A retry that tried every span, the numberless title and the swap and found nothing leaves the read's NotFound as it was (the same answer). A retry the
    /// budget cut short does not: if a span, the numberless title or the swap was left untried because the time ran out, or an attempt's providers could not
    /// answer (they failed, or the budget cancelled the call), the read is <b>Pending</b>, not a final NotFound. A live <c>/identify</c> request has little
    /// time, and its Pending row is finished by the background job (<c>ResolvePendingIdentify</c> runs <see cref="GroundAsync"/> again
    /// with the job's much larger budget), so <c>/identify</c> ends up with the answer the eval, with its patient budget, measures.
    /// The Pending answer's trace ends with a <c>trimmed</c> step, <c>cut_short</c>.
    /// </para>
    /// <para>
    /// The database step takes one call at a time. Reads that retry together in one batch share it; a caller that runs several
    /// batches at once (the eval grounder, one batch per read) gives <paramref name="databaseGate"/> so their catalogue calls queue.
    /// </para>
    /// <para>
    /// What a span, the numberless title or the swap finds is offered, never decided: the verdict is capped at Ambiguous and flagged
    /// (<see cref="BarcodeResolution.FoundByTrimmedSpan"/>), so the person picks and such a find can neither be added by itself,
    /// nor become a title nobody read, nor confirm another engine's read. The trace says which span found it and which words it
    /// dropped (a <c>trimmed</c> step), which sequel number was dropped (a <c>numeral</c> step), or that the creator as read was asked
    /// as the title (a <c>swapped</c> step).
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<BarcodeResolution>> RetryTrimmedAsync(
        IReadOnlyList<TrimmedRetry> reads, CancellationToken cancellationToken, SemaphoreSlim? databaseGate = null)
    {
        var answers = reads.Select(r => r.Answer).ToArray();
        var attempts = reads.Select(r => AttemptsFor(r.Facts, r.Profile, r.Answer)).ToArray();
        var cutShort = new bool[reads.Count];
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
            var scored = await Task.WhenAll(looking.Select(async (i, n) => Score(asked[n], reads[i].Profile, pools[n], await reads[i].Ask(asked[n]))));

            var next = new List<int>(looking.Count);
            for (var n = 0; n < looking.Count; n++)
            {
                var i = looking[n];
                if (scored[n].Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous)
                {
                    answers[i] = Offered(reads[i].Answer, scored[n], attempts[i][round].Step);
                }
                else
                {
                    // An attempt the providers could not answer (they failed, or the budget cancelled the call) was not really tried.
                    cutShort[i] |= scored[n].Verdict == ResolutionVerdict.Pending;
                    next.Add(i);
                }
            }

            looking = next;
        }

        for (var i = 0; i < answers.Length; i++)
        {
            if (cutShort[i] && ReferenceEquals(answers[i], reads[i].Answer))
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

        var titles = await BestTitlesAsync(reads, localTitles, cancellationToken);
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
        IReadOnlyList<SeriesRetry> reads,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, string>>> localTitles,
        CancellationToken cancellationToken)
    {
        var titles = new string?[reads.Count];
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

    /// <summary>What a span, the numberless title, the swap or the neighbour's series found, as an answer for the read it was cut from: the same releases, never Matched, and a step to say why.</summary>
    private static BarcodeResolution Offered(BarcodeResolution original, BarcodeResolution found, TraceStep why) => found with
    {
        Verdict = ResolutionVerdict.Ambiguous,
        FoundByTrimmedSpan = true,
        Steps = [.. original.Steps, why, .. found.Steps],
    };
}
