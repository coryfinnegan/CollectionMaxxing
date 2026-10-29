using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CollectionMaxxing.Application.Tests.Scanning;

/// <summary>
/// A provider that cannot be reached gives up quietly: it answers "nothing" and reports the failure, never throws. The search
/// must still hear it, so the read is asked again later (Pending) instead of being called unknown, and the down provider is not
/// asked down the rest of its retry ladder.
/// </summary>
public class TextSearchOutageTests
{
    private const string Title = "The Glass Orchard at Night";
    private const string Author = "Wren Halloway";

    private static ProviderRelease Book(string provider = "googlebooks") => new()
    {
        Provider = provider,
        ExternalId = $"{provider}-glass-orchard",
        Title = Title,
        Kind = WorkKind.Book,
        Year = 2011,
        Creators = [Author],
    };

    /// <summary>
    /// A book provider that answers from a script and remembers every query. <c>null</c> from the script is an outage, the way
    /// the real adapters have one: the failure is reported and the answer is empty. Every call awaits <paramref name="gate"/>
    /// first, when given, so concurrent providers are in flight at once.
    /// </summary>
    private sealed class ScriptedProvider(string name, Func<CatalogQuery, IReadOnlyList<ProviderRelease>?> answer, Func<Task>? gate = null)
        : ICatalogProvider
    {
        public List<CatalogQuery> Queries { get; } = [];

        public string Name => name;

        public bool Supports(WorkKind kind) => kind == WorkKind.Book;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            lock (Queries)
            {
                Queries.Add(query);
            }

            await Task.Yield();
            if (gate is not null)
            {
                await gate();
            }

            if (answer(query) is { } found)
            {
                return found;
            }

            ProviderCalls.ReportFailure();
            return [];
        }
    }

    private static TextSearchResolver Resolver(params ICatalogProvider[] providers) => new(
        null!, null!, providers, new ScanningOptions(), NullLogger<TextSearchResolver>.Instance);

    private static CatalogQuery Query() => new() { Title = Title, Creator = Author, Kind = WorkKind.Book };

    private static readonly ReadItem Read = new() { Title = Title, Creator = Author, Kind = WorkKind.Book };

    private static readonly ScoringProfile Spine = new ScoringOptions().Profile(ScoringProfileKind.ShelfSpine);

    [Fact]
    public async Task A_provider_that_could_not_be_reached_makes_the_search_report_a_failure()
    {
        var down = new ScriptedProvider("openlibrary", _ => null);

        var answers = await Resolver(down).AskProvidersAsync(Query(), CancellationToken.None, CancellationToken.None);

        answers.AnyFailed.ShouldBeTrue();
        answers.Found.ShouldBeEmpty();
        answers.Steps.ShouldHaveSingleItem().ShouldBe(new TraceStep("openlibrary", "failed", answers.Steps[0].Ms));
    }

    [Fact]
    public async Task A_provider_that_could_not_be_reached_is_not_asked_down_the_rest_of_its_ladder()
    {
        // A title with an author and words to drop has four rungs: an outage used to spend all of them (8 calls at Open Library).
        var down = new ScriptedProvider("openlibrary", _ => null);

        await Resolver(down).AskProvidersAsync(Query(), CancellationToken.None, CancellationToken.None);

        down.Queries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_provider_that_answered_nothing_is_a_miss_not_a_failure()
    {
        var empty = new ScriptedProvider("openlibrary", _ => []);

        var answers = await Resolver(empty).AskProvidersAsync(Query(), CancellationToken.None, CancellationToken.None);

        answers.AnyFailed.ShouldBeFalse();
        answers.Steps.ShouldAllBe(step => step.Outcome == "miss");
        empty.Queries.Count.ShouldBeGreaterThan(1); // a plain empty answer still goes down the ladder
    }

    [Fact]
    public async Task A_down_provider_does_not_mark_a_healthy_one_asked_at_the_same_time()
    {
        // Both first rungs are in flight together when the down provider reports its failure.
        var bothAsking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        Task Gate()
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothAsking.SetResult();
            }

            return bothAsking.Task;
        }

        var book = Book();
        var down = new ScriptedProvider("openlibrary", _ => null, Gate);
        var healthy = new ScriptedProvider("googlebooks", query => query.Creator is null ? [book] : [], Gate);

        var answers = await Resolver(down, healthy).AskProvidersAsync(Query(), CancellationToken.None, CancellationToken.None);

        answers.AnyFailed.ShouldBeTrue();
        answers.Found.ShouldBe([book]);
        down.Queries.Count.ShouldBe(1);
        healthy.Queries.Count.ShouldBe(2); // its qualified rung missed, so it fell back to the title alone and found the book
        answers.Steps.Where(s => s.Provider == "openlibrary").Select(s => s.Outcome).ShouldBe(["failed"]);
        answers.Steps.Where(s => s.Provider == "googlebooks").Select(s => s.Outcome).ShouldBe(["miss", "hit"]);
    }

    [Fact]
    public async Task A_read_that_found_nothing_while_a_provider_was_down_is_pending_not_unknown()
    {
        var down = new ScriptedProvider("openlibrary", _ => null);
        var empty = new ScriptedProvider("googlebooks", _ => []);

        var answers = await Resolver(down, empty).AskProvidersAsync(Query(), CancellationToken.None, CancellationToken.None);
        var resolution = TextSearchResolver.Score(Read, Spine, LocalPool.Empty, answers, pendingWhenProvidersFailed: true);

        resolution.Verdict.ShouldBe(ResolutionVerdict.Pending);
    }

    [Fact]
    public async Task A_read_that_found_nothing_with_every_provider_up_is_not_found()
    {
        var empty = new ScriptedProvider("openlibrary", _ => []);

        var answers = await Resolver(empty).AskProvidersAsync(Query(), CancellationToken.None, CancellationToken.None);
        var resolution = TextSearchResolver.Score(Read, Spine, LocalPool.Empty, answers, pendingWhenProvidersFailed: true);

        resolution.Verdict.ShouldBe(ResolutionVerdict.NotFound);
    }

    [Fact]
    public async Task A_book_found_by_one_provider_still_matches_while_another_is_down()
    {
        var down = new ScriptedProvider("openlibrary", _ => null);
        var healthy = new ScriptedProvider("googlebooks", _ => [Book()]);

        var answers = await Resolver(down, healthy).AskProvidersAsync(Query(), CancellationToken.None, CancellationToken.None);
        var resolution = TextSearchResolver.Score(Read, Spine, LocalPool.Empty, answers, pendingWhenProvidersFailed: true);

        resolution.Verdict.ShouldBe(ResolutionVerdict.Matched);
    }
}
