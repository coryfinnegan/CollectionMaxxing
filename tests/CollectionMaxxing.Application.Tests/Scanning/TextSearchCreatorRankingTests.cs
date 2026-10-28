using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Scanning;

/// <summary>
/// Stage 1 (#438): candidates rank by combined title + author agreement. Two books sharing a title but not an
/// author both survive (neither is the other's duplicate), and the OCR author's book sorts first.
/// </summary>
public class TextSearchCreatorRankingTests
{
    private static readonly ScoringProfile Cover = new ScoringOptions().Profile(ScoringProfileKind.Cover);

    private static ProviderRelease Book(string key, string creator, int year) => new()
    {
        Provider = "openlibrary",
        ExternalId = key,
        Title = "Nightfall",
        Kind = WorkKind.Book,
        Year = year,
        Creators = [creator],
    };

    [Fact]
    public void A_title_shared_by_two_authors_ranks_the_ocr_authors_book_first()
    {
        var read = new ReadItem { Title = "Nightfall", Kind = WorkKind.Book, Creator = "Isaac Asimov" };
        var answers = new ProviderAnswers(
            [Book("asimov", "Isaac Asimov", 1990), Book("other", "Somebody Else", 1990)],
            [new TraceStep("openlibrary", "hit", 0)],
            AnyFailed: false);

        var resolution = TextSearchResolver.Score(read, Cover, LocalPool.Empty, answers, pendingWhenProvidersFailed: false);

        resolution.Options.Count.ShouldBe(2);
        resolution.Options[0].Provider!.Creators.ShouldBe(["Isaac Asimov"]);
        resolution.Options[1].Provider!.Creators.ShouldBe(["Somebody Else"]);
    }
}
