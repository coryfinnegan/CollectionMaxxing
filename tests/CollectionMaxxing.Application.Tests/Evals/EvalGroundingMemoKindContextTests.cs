using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>
/// A read's kind context (the kind its snap agreed on) is part of what it is grounded with, so the eval run's memo, which keys on
/// the read's value, never answers a read from a lookup made under another snap's kind.
/// </summary>
public class EvalGroundingMemoKindContextTests
{
    private static ReadFacts Words(WorkKind? kindContext) =>
        new("Quiet Harbor Atlas", "Quiet Harbor Atlas", null, null, null, null, null, null, null, KindContext: kindContext);

    [Fact]
    public async Task The_same_words_in_a_snap_of_films_and_in_a_snap_of_books_are_grounded_twice()
    {
        var memo = new EvalGroundingMemo();
        var calls = 0;
        Task<BarcodeResolution> Ask()
        {
            calls++;
            return Task.FromResult(new BarcodeResolution(ResolutionVerdict.Ambiguous, [], null, []));
        }

        await memo.GroundAsync(Words(WorkKind.Film), ScoringProfileKind.ShelfSpine, Ask);
        await memo.GroundAsync(Words(WorkKind.Book), ScoringProfileKind.ShelfSpine, Ask);
        await memo.GroundAsync(Words(null), ScoringProfileKind.ShelfSpine, Ask);

        calls.ShouldBe(3);
    }

    [Fact]
    public async Task The_same_words_in_two_snaps_of_films_are_grounded_once()
    {
        var memo = new EvalGroundingMemo();
        var calls = 0;
        Task<BarcodeResolution> Ask()
        {
            calls++;
            return Task.FromResult(new BarcodeResolution(ResolutionVerdict.Ambiguous, [], null, []));
        }

        await memo.GroundAsync(Words(WorkKind.Film), ScoringProfileKind.ShelfSpine, Ask);
        await memo.GroundAsync(Words(WorkKind.Film), ScoringProfileKind.ShelfSpine, Ask);

        calls.ShouldBe(1);
    }
}
