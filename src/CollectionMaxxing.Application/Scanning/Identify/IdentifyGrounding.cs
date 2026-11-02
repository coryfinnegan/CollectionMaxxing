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
    WorkKind? KindContext = null)
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
    /// found nothing is then asked again without the stray words at its ends (<see cref="RetryTrimmedAsync"/>), within the same budget.
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
        if (!WantsTrimmedRetry(facts, profile, answer))
        {
            return answer;
        }

        // A spine that found nothing is asked again without the stray words at its ends, within the budget it has.
        var retried = await RetryTrimmedAsync(
            [new TrimmedRetry(facts, profile, answer, spanFacts => AskProvidersAsync(spanFacts, cancellationToken, timeout.Token), timeout.Token)],
            cancellationToken);
        return retried[0];
    }

    /// <summary>
    /// Whether <see cref="RetryTrimmedAsync"/> may help a read: it was scored as a spine on a shelf, nothing was found for it (a
    /// Pending answer, where a provider could not answer, is asked again later instead, and an Ambiguous or Matched one has
    /// found its release), and its title has words enough to drop some (<see cref="TrimmedSpans"/>). Never a read whose title a
    /// printed product code decided (ADR-031): that title is the game table's, not words Vision ran stray print into.
    /// </summary>
    public static bool WantsTrimmedRetry(ReadFacts facts, ScoringProfileKind profile, BarcodeResolution answer) =>
        profile == ScoringProfileKind.ShelfSpine
        && answer.Verdict == ResolutionVerdict.NotFound
        && facts.ProductCode is null
        && TrimmedSpans.For(facts.SearchText).Count > 0;

    /// <summary>
    /// A spine read that found nothing is asked again without the stray words at its ends (<see cref="TrimmedSpans"/>): Vision reads
    /// an author's name or OCR junk into the title, and the whole string then matches nothing while the rest of it matches a book.
    /// The answers come back in the order of <paramref name="reads"/>, each the one it had unless a span found a release (or the
    /// budget cut the retry short, which makes it Pending).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spans are tried round by round, for all the reads still looking at once: one catalogue call for the round (the database
    /// step takes one batch at a time), then the providers for every read of the round together, through each read's own
    /// <see cref="TrimmedRetry.Ask"/>. A span is searched and scored as the read's title, with the read's other facts unchanged
    /// (<see cref="ReadFacts.Text"/> stays what was read). A read stops at the first span whose grounding names a release at or
    /// above the Ambiguous threshold; at most <see cref="TrimmedSpans.MaxSpans"/> spans are asked for a read, and none once its
    /// <see cref="TrimmedRetry.Budget"/> has run out.
    /// </para>
    /// <para>
    /// A retry that tried every span and found nothing leaves the read's NotFound as it was (the same answer). A retry the budget cut
    /// short does not: if a span was left untried because the time ran out, or a span's providers could not answer (they failed, or
    /// the budget cancelled the call), the read is <b>Pending</b>, not a final NotFound. A live <c>/identify</c> request has little
    /// time, and its Pending row is finished by the background job (<c>ResolvePendingIdentify</c> runs <see cref="GroundAsync"/> again
    /// with the job's much larger budget), so <c>/identify</c> ends up with the answer the eval, with its patient budget, measures.
    /// The Pending answer's trace ends with a <c>trimmed</c> step, <c>cut_short</c>.
    /// </para>
    /// <para>
    /// The database step takes one call at a time. Reads that retry together in one batch share it; a caller that runs several
    /// batches at once (the eval grounder, one batch per read) gives <paramref name="databaseGate"/> so their catalogue calls queue.
    /// </para>
    /// <para>
    /// What a span finds is offered, never decided: the verdict is capped at Ambiguous, so the person picks and a trimmed find can
    /// neither be added by itself nor become a title nobody read. The trace says which span found it and which words it dropped.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<BarcodeResolution>> RetryTrimmedAsync(
        IReadOnlyList<TrimmedRetry> reads, CancellationToken cancellationToken, SemaphoreSlim? databaseGate = null)
    {
        var answers = reads.Select(r => r.Answer).ToArray();
        var spans = reads.Select(r => WantsTrimmedRetry(r.Facts, r.Profile, r.Answer) ? TrimmedSpans.For(r.Facts.SearchText) : []).ToArray();
        var cutShort = new bool[reads.Count];
        var looking = Enumerable.Range(0, reads.Count).Where(i => spans[i].Count > 0).ToList();
        for (var round = 0; round < TrimmedSpans.MaxSpans && looking.Count > 0; round++)
        {
            // A read with a span left to try and no time to try it in has not finished: its NotFound is not the final word.
            var open = new List<int>(looking.Count);
            foreach (var i in looking.Where(i => round < spans[i].Count))
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

            var asked = looking.Select(i => reads[i].Facts with { Title = spans[i][round].Text }).ToList();
            var pools = await FindLocalGatedAsync(asked, databaseGate, cancellationToken);
            var scored = await Task.WhenAll(looking.Select(async (i, n) => Score(asked[n], reads[i].Profile, pools[n], await reads[i].Ask(asked[n]))));

            var next = new List<int>(looking.Count);
            for (var n = 0; n < looking.Count; n++)
            {
                var i = looking[n];
                if (scored[n].Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous)
                {
                    answers[i] = Offered(reads[i].Answer, scored[n], spans[i][round]);
                }
                else
                {
                    // A span the providers could not answer (they failed, or the budget cancelled the call) was not really tried.
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

    /// <summary>What a span found, as an answer for the read it was cut from: the same releases, never Matched, and a step to say why.</summary>
    private static BarcodeResolution Offered(BarcodeResolution original, BarcodeResolution found, TrimmedSpan span) => found with
    {
        Verdict = ResolutionVerdict.Ambiguous,
        FoundByTrimmedSpan = true,
        Steps = [.. original.Steps, new TraceStep("trimmed", $"{span.Text} (dropped: {span.Dropped})", 0), .. found.Steps],
    };
}
