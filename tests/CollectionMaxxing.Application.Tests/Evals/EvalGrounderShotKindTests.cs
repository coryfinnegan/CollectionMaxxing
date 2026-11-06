using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Evals;

// The eval grounds a shelf the way /identify does, and that includes the pass over the whole photo: a spine whose read found
// another kind than the shelf's ("cnoi Emergency Contact o" beside books, which found three films) is asked again as the shelf's
// kind. The answer depends on the other spines, so the run's memo, which remembers a read on its own, never holds it. The reads
// are the misreads of a stored eval; the providers are scripted and know their own kind only, as the real ones do.
public class EvalGrounderShotKindTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private sealed class NoSerials : IGameSerialLookup
    {
        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null) => [];
    }

    private static string Key(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static ProviderRelease Release(string title, WorkKind kind, string id, params string[] creators) => new()
    {
        Provider = kind == WorkKind.Book ? "books" : "films",
        ExternalId = id,
        Title = title,
        Kind = kind,
        Format = kind == WorkKind.Book ? ReleaseFormat.Paperback : ReleaseFormat.Dvd,
        Year = 2019,
        Creators = creators,
    };

    /// <summary>
    /// A provider of one kind. It knows the releases it was told for exactly one asked title, in any ask or only in an ask for its own
    /// kind (<see cref="Know"/>), and can hang on one title. Remembers what it was asked.
    /// </summary>
    private sealed class OfKind(WorkKind kind) : ICatalogProvider
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (ProviderRelease[] Releases, bool OnlyAsItsKind)> _known = [];
        private readonly List<(string Title, WorkKind? Kind)> _asked = [];

        public string Name => kind == WorkKind.Book ? "books" : "films";

        public string? Hangs { get; set; }

        public List<(string Title, WorkKind? Kind)> Asked
        {
            get
            {
                lock (_gate)
                {
                    return [.. _asked];
                }
            }
        }

        public bool Supports(WorkKind asked) => kind == WorkKind.Book ? asked == WorkKind.Book : asked is WorkKind.Film or WorkKind.Tv;

        public OfKind Know(string asked, bool onlyAsItsKind, params ProviderRelease[] releases)
        {
            _known[Key(asked)] = (releases, onlyAsItsKind);
            return this;
        }

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            lock (_gate)
            {
                _asked.Add((query.Title, query.Kind));
            }

            if (Hangs is { } hangs && Key(hangs) == Key(query.Title) && query.Kind == kind)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return _known.TryGetValue(Key(query.Title), out var found) && (!found.OnlyAsItsKind || query.Kind == kind) ? found.Releases : [];
        }
    }

    /// <summary>
    /// A shelf of books the providers know, and the read that finds only films until it is asked as a book. The films are titled
    /// <paramref name="filmTitle"/>: the book's own title, which the read matches just as well, or a looser one.
    /// </summary>
    private static (OfKind Books, OfKind Films) Providers(string filmTitle = "Emergency Contact")
    {
        var books = new OfKind(WorkKind.Book)
            .Know("A COURT OF MIST AND FURY", false, Release("A Court of Mist and Fury", WorkKind.Book, "m1", "Sarah J. Maas"))
            .Know("A COURT OF WINGS AND RUIN", false, Release("A Court of Wings and Ruin", WorkKind.Book, "m2", "Sarah J. Maas"))
            .Know("ASSASSINS APPRENTICE", false, Release("Assassins Apprentice", WorkKind.Book, "m3", "Robin Hobb"))
            .Know("cnoi Emergency Contact o", true, Release("Emergency Contact", WorkKind.Book, "b1"));
        var films = new OfKind(WorkKind.Film)
            .Know(
                "cnoi Emergency Contact o",
                false,
                Release(filmTitle, WorkKind.Film, "f1") with { Year = 2019 },
                Release(filmTitle, WorkKind.Film, "f2") with { Year = 2020 },
                Release(filmTitle, WorkKind.Film, "f3") with { Year = 2021 });
        return (books, films);
    }

    private static EvalReadItemInput Spine(string title, string? author = null) => new(
        new IdentifyItemInput(null, null, title, title, author, null, null, null, null, null, null, null), null);

    /// <summary>Three spines that name their author, which is what lets a book be Matched, and the read that names none.</summary>
    private static EvalReadItemInput[] BookShelf() =>
        [Spine("A COURT OF MIST AND FURY", "Sarah J. Maas"), Spine("A COURT OF WINGS AND RUIN", "Sarah J. Maas"), Spine("ASSASSINS APPRENTICE", "Robin Hobb"), Spine("cnoi Emergency Contact o")];

    private static EvalGrounder Grounder(params OfKind[] providers)
    {
        var scanning = new ScanningOptions { JobResolveBudget = TimeSpan.FromSeconds(30) };
        var resolver = new TextSearchResolver(null!, new Catalogue(), providers, scanning, NullLogger<TextSearchResolver>.Instance);
        return new EvalGrounder(
            new IdentifyGrounding(resolver, new FixedScoring()), null!, scanning,
            Options.Create(new EvalsOptions { GroundingParallelism = 4 }), new NoSerials(), TimeProvider.System);
    }

    private static Task<IReadOnlyList<GroundedItem>> GroundAsync(
        EvalGrounder grounder, EvalGroundingMemo memo, WorkKind? printKind, params EvalReadItemInput[] reads) =>
        grounder.GroundAsync(ScanEngine.VisionPipeline, reads, memo, printKind, CancellationToken.None).WaitAsync(Patience);

    [Fact]
    public async Task A_read_beside_matched_books_that_found_three_looser_films_is_offered_the_book_first_and_a_film_after()
    {
        var (books, films) = Providers("Emergency Contact Live");

        var items = await GroundAsync(Grounder(books, films), new EvalGroundingMemo(), null, BookShelf());

        items.Take(3).Select(i => i.Verdict).ShouldAllBe(v => v == "matched");
        items[3].Verdict.ShouldBe("ambiguous");
        items[3].Candidates.Select(c => c.Kind).ShouldBe(["book", "film", "film"]);
        items[3].Candidates[0].Title.ShouldBe("Emergency Contact");
        books.Asked.ShouldContain(("cnoi Emergency Contact o", WorkKind.Book)); // and it was the ask as a book that found it
    }

    // A spine read that only found itself (a format banner) is left out of the score, as a repeat is.

    [Fact]
    public async Task A_spine_read_of_a_format_banner_that_found_only_other_titles_is_not_scored()
    {
        var (books, films) = Providers();
        books.Know("HOME VIDEO", false, Release("Home Video Nights", WorkKind.Book, "n1"), Release("Home Video Stories", WorkKind.Book, "n2"));
        var reads = BookShelf()[..3].Append(Spine("HOME VIDEO")).ToArray();

        var shot = await Grounder(books, films).GroundWithSkipsAsync(
            ScanEngine.VisionPipeline, reads, new EvalGroundingMemo(), null, CancellationToken.None).WaitAsync(Patience);

        shot.Items.Count.ShouldBe(3);
        shot.Items.ShouldNotContain(i => i.ReadTitle == "HOME VIDEO");
        shot.SkippedSegments.ShouldBe(1);
    }

    [Fact]
    public async Task The_eval_does_not_score_a_skipped_unfound_spine()
    {
        var (books, films) = Providers();
        var reads = BookShelf()[..3].Append(Spine("ZORBLAX QUILLWORTH NOTHING")).ToArray();

        var shot = await Grounder(books, films).GroundWithSkipsAsync(
            ScanEngine.VisionPipeline, reads, new EvalGroundingMemo(), null, CancellationToken.None).WaitAsync(Patience);

        shot.Items.Count.ShouldBe(3);
        shot.Items.ShouldNotContain(i => i.ReadTitle == "ZORBLAX QUILLWORTH NOTHING");
        shot.SkippedSegments.ShouldBe(1);
    }

    [Fact]
    public async Task A_book_that_is_only_as_good_a_title_match_as_the_films_the_read_found_is_offered_first_on_a_shelf_of_books()
    {
        var (books, films) = Providers();

        var items = await GroundAsync(Grounder(books, films), new EvalGroundingMemo(), null, BookShelf());

        items[3].Verdict.ShouldBe("ambiguous");
        items[3].Candidates.Select(c => c.Kind).ShouldBe(["book", "film", "film"]);
        books.Asked.ShouldContain(("cnoi Emergency Contact o", WorkKind.Book));
    }

    [Fact]
    public async Task The_kinds_the_models_reads_of_the_same_snap_name_decide_the_shot_the_vision_reads_are_asked_as()
    {
        var (books, films) = Providers();
        var grounder = Grounder(books, films);
        EvalReadItemInput[] visionReads = [Spine("cnoi Emergency Contact o"), Spine("GAMMA QUEST"), Spine("DELTA QUEST"), Spine("EPSILON QUEST")];

        var alone = await grounder.GroundAsync(ScanEngine.VisionPipeline, visionReads, new EvalGroundingMemo(), null, CancellationToken.None).WaitAsync(Patience);
        var withTheModel = await grounder.GroundAsync(
            ScanEngine.VisionPipeline, visionReads, new EvalGroundingMemo(), null, CancellationToken.None,
            [WorkKind.Book, WorkKind.Book, WorkKind.Book, WorkKind.Book]).WaitAsync(Patience);

        alone[0].Candidates.Select(c => c.Kind).ShouldBe(["film", "film", "film"]); // no kind for the snap: nothing asked
        withTheModel[0].Candidates.Select(c => c.Kind).ShouldBe(["book", "film", "film"]);
    }

    [Fact]
    public async Task The_shelfs_own_print_is_enough_when_no_spine_matched()
    {
        var (books, films) = Providers();

        var items = await GroundAsync(Grounder(books, films), new EvalGroundingMemo(), WorkKind.Book, Spine("cnoi Emergency Contact o"), Spine("GAMMA QUEST"), Spine("DELTA QUEST"), Spine("EPSILON QUEST"));

        items[0].Candidates.Select(c => c.Kind).ShouldBe(["book", "film", "film"]);
    }

    [Fact]
    public async Task A_shelf_of_films_asks_nothing_as_a_book()
    {
        var (books, films) = Providers();

        var items = await GroundAsync(Grounder(books, films), new EvalGroundingMemo(), WorkKind.Film, Spine("cnoi Emergency Contact o"), Spine("GAMMA QUEST"), Spine("DELTA QUEST"), Spine("EPSILON QUEST"));

        items[0].Candidates.Select(c => c.Kind).ShouldBe(["film", "film", "film"]);
        books.Asked.ShouldNotContain(ask => ask.Kind == WorkKind.Book);
    }

    [Fact]
    public async Task The_run_never_remembers_the_kind_answer_for_the_read_on_its_own()
    {
        var (books, films) = Providers();
        var grounder = Grounder(books, films);
        var memo = new EvalGroundingMemo();

        var onTheShelf = await GroundAsync(grounder, memo, null, BookShelf());
        var alone = await GroundAsync(grounder, memo, null, Spine("cnoi Emergency Contact o"), Spine("GAMMA QUEST"), Spine("DELTA QUEST"), Spine("EPSILON QUEST"));

        onTheShelf[3].Candidates.Select(c => c.Kind).ShouldBe(["book", "film", "film"]);
        alone[0].Candidates.Select(c => c.Kind).ShouldBe(["film", "film", "film"]); // the read as it stands alone, from the memo
        memo.Reused.ShouldBeGreaterThan(0);
    }

    // The pass has its own fixed budget (ShotKindRule.Budget), the one /identify gives it, not the patient one the first asks get: the
    // eval measures what a person scanning could be given. A pass that runs out leaves every answer as it was.

    [Fact]
    public async Task A_pass_that_ran_out_of_time_leaves_the_read_with_its_films_after_the_live_budget_not_the_jobs()
    {
        var (books, films) = Providers();
        books.Hangs = "cnoi Emergency Contact o"; // only the ask as a book hangs
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var items = await GroundAsync(Grounder(books, films), new EvalGroundingMemo(), null, BookShelf());

        items[3].Verdict.ShouldBe("ambiguous");
        items[3].Candidates.Select(c => c.Kind).ShouldBe(["film", "film", "film"]);
        books.Asked.ShouldContain(("cnoi Emergency Contact o", WorkKind.Book));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10)); // the job budget of the grounder is 30 s; the pass took its own
    }

    [Fact]
    public async Task An_ask_cut_short_by_cancelling_is_not_an_answer()
    {
        var (books, films) = Providers();
        books.Hangs = "cnoi Emergency Contact o";
        using var cancel = new CancellationTokenSource();

        var grounding = Grounder(books, films).GroundAsync(ScanEngine.VisionPipeline, BookShelf(), new EvalGroundingMemo(), printKind: null, cancel.Token);
        var deadline = DateTime.UtcNow + Patience;
        while (!books.Asked.Contains(("cnoi Emergency Contact o", WorkKind.Book)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => grounding.WaitAsync(Patience));
    }
}
