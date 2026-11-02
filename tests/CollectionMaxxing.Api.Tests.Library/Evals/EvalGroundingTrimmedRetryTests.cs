using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The eval measures what <c>/identify</c> does: a spine read that found nothing is asked again without the stray words at its
/// ends by the one piece of code both run, so the same shelf gets the same verdicts and the same releases from either. Real
/// Postgres for the local catalogue (the trigram search that finds a book by a span), no provider that knows a book.
/// </summary>
public class EvalGroundingTrimmedRetryTests(ApiFixture api) : EvalTest(api)
{
    private const string Junk = "XQZTRWVKLMNBHDPLMKJNHB IRON FOX";

    private static readonly string[] Titles = ["Alpha Quest", "Beta Quest", "Gamma Quest", Junk];

    private static IdentifyItemInput Item(string title) => new(
        null, null, title, title, null, null, null, WorkKind.Book, null, null, null, null);

    private async Task<IReadOnlyList<GroundedItem>> GroundedByTheEvalAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var grounder = scope.ServiceProvider.GetRequiredService<EvalGrounder>();
        return await grounder.GroundAsync(
            ScanEngine.VisionPipeline, [.. Titles.Select(title => new EvalReadItemInput(Item(title), null))], new EvalGroundingMemo(),
            printKind: null, CancellationToken.None);
    }

    [Fact]
    public async Task The_eval_offers_the_book_a_trimmed_span_found_and_does_not_match_it()
    {
        await CatalogSeed.AddAsync(Db, "Iron Fox", WorkKind.Book, 1999, format: ReleaseFormat.Paperback);

        var items = await GroundedByTheEvalAsync();

        items.Select(i => i.Read!.Profile).ShouldAllBe(profile => profile == "shelf_spine");
        items[3].Verdict.ShouldBe("ambiguous");
        items[3].Candidates.Select(c => c.Title).ShouldBe(["Iron Fox"]);
        items.Take(3).Select(i => i.Verdict).ShouldAllBe(v => v == "not_found");
    }

    [Fact]
    public async Task The_eval_and_identify_give_the_same_shelf_the_same_verdicts_and_the_same_releases()
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Iron Fox", WorkKind.Book, 1999, format: ReleaseFormat.Paperback);
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);

        var live = await IdentifyOkAsync(client, sessionId, [.. Titles.Select(title => (object)Read(title: title, kind: "book"))]);
        var eval = await GroundedByTheEvalAsync();

        eval.Select(i => i.Verdict).ShouldBe(live.Select(Resolution));
        eval.Select(i => i.Candidates.Select(c => c.Title).ToList()).ShouldBe(live.Select(AlternativeTitles));
        Resolution(live[3]).ShouldBe("ambiguous"); // and it was the retry that found the book
    }
}
