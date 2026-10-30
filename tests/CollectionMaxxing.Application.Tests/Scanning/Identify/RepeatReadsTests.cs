using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// One book, one candidate per photo: a spine the engine read twice (once clean, once with OCR junk on the end) points at
// one release both times, and only the strongest of those reads stands. Neutral titles throughout.
public class RepeatReadsTests
{
    private static readonly Guid BookA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid BookB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static BarcodeResolution Local(ResolutionVerdict verdict, Guid release, double score) =>
        new(verdict, [new ResolutionOption(release, null, score)], null, []);

    private static BarcodeResolution Provided(ResolutionVerdict verdict, string provider, string externalId, double score) =>
        new(verdict, [new ResolutionOption(null, new ProviderRelease { Provider = provider, ExternalId = externalId, Title = "A Title", Kind = WorkKind.Book }, score)], null, []);

    [Fact]
    public void A_matched_read_beats_an_ambiguous_repeat_of_the_same_release_even_when_the_repeat_came_first()
    {
        var answers = new[] { Local(ResolutionVerdict.Ambiguous, BookA, 0.95), Local(ResolutionVerdict.Matched, BookA, 0.92) };

        RepeatReads.Of(answers).ShouldBe([0]);
    }

    [Fact]
    public void A_matched_read_keeps_its_place_against_a_later_ambiguous_repeat()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.92), Local(ResolutionVerdict.Ambiguous, BookA, 0.7) };

        RepeatReads.Of(answers).ShouldBe([1]);
    }

    [Fact]
    public void Between_two_ambiguous_reads_of_one_release_the_higher_score_stands()
    {
        var answers = new[] { Local(ResolutionVerdict.Ambiguous, BookA, 0.6), Local(ResolutionVerdict.Ambiguous, BookA, 0.7) };

        RepeatReads.Of(answers).ShouldBe([0]);
    }

    [Fact]
    public void Between_two_matched_reads_of_one_release_the_higher_score_stands()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.91), Local(ResolutionVerdict.Matched, BookA, 0.99) };

        RepeatReads.Of(answers).ShouldBe([0]);
    }

    [Fact]
    public void A_tie_keeps_the_earlier_read()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Local(ResolutionVerdict.Matched, BookA, 0.95) };

        RepeatReads.Of(answers).ShouldBe([1]);
    }

    [Fact]
    public void Three_reads_of_one_release_leave_exactly_one()
    {
        var answers = new[]
        {
            Local(ResolutionVerdict.Ambiguous, BookA, 0.8),
            Local(ResolutionVerdict.Matched, BookA, 0.93),
            Local(ResolutionVerdict.Matched, BookA, 0.97),
        };

        RepeatReads.Of(answers).ShouldBe([0, 1]);
    }

    [Fact]
    public void Reads_whose_best_options_are_different_releases_stay_apart()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Local(ResolutionVerdict.Matched, BookB, 0.95) };

        RepeatReads.Of(answers).ShouldBeEmpty();
    }

    [Fact]
    public void Two_reads_that_only_share_a_runner_up_stay_apart()
    {
        var first = new BarcodeResolution(
            ResolutionVerdict.Ambiguous, [new ResolutionOption(BookA, null, 0.8), new ResolutionOption(BookB, null, 0.78)], null, []);
        var second = new BarcodeResolution(
            ResolutionVerdict.Ambiguous, [new ResolutionOption(BookB, null, 0.82), new ResolutionOption(BookA, null, 0.7)], null, []);

        RepeatReads.Of([first, second]).ShouldBeEmpty();
    }

    [Fact]
    public void The_same_provider_and_external_id_is_one_release()
    {
        var answers = new[]
        {
            Provided(ResolutionVerdict.Ambiguous, "openlibrary", "OL1W", 0.7),
            Provided(ResolutionVerdict.Matched, "openlibrary", "OL1W", 0.95),
        };

        RepeatReads.Of(answers).ShouldBe([0]);
    }

    [Fact]
    public void The_same_external_id_at_another_provider_is_another_release()
    {
        var answers = new[]
        {
            Provided(ResolutionVerdict.Matched, "openlibrary", "1", 0.95),
            Provided(ResolutionVerdict.Matched, "googlebooks", "1", 0.95),
        };

        RepeatReads.Of(answers).ShouldBeEmpty();
    }

    [Fact]
    public void A_catalogue_release_and_a_provider_release_are_not_the_same_release()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Provided(ResolutionVerdict.Matched, "openlibrary", "OL1W", 0.95) };

        RepeatReads.Of(answers).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ResolutionVerdict.NotFound)]
    [InlineData(ResolutionVerdict.Pending)]
    public void A_read_with_no_answer_is_never_a_repeat_and_never_makes_one(ResolutionVerdict verdict)
    {
        var answers = new[]
        {
            new BarcodeResolution(verdict, [], null, []),
            Local(ResolutionVerdict.Matched, BookA, 0.95),
            new BarcodeResolution(verdict, [], null, []),
        };

        RepeatReads.Of(answers).ShouldBeEmpty();
    }

    [Fact]
    public void Unrelated_reads_in_between_do_not_hide_a_repeat()
    {
        var answers = new[]
        {
            Local(ResolutionVerdict.Matched, BookA, 0.95),
            Local(ResolutionVerdict.Matched, BookB, 0.95),
            Local(ResolutionVerdict.Ambiguous, BookA, 0.75),
        };

        RepeatReads.Of(answers).ShouldBe([2]);
    }

    [Fact]
    public void Nothing_to_compare_is_no_repeats()
    {
        RepeatReads.Of([]).ShouldBeEmpty();
    }
}
