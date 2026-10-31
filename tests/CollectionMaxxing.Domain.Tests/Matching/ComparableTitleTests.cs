using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

public class ComparableTitleTests
{
    [Fact]
    public void Its_key_is_the_normalized_title()
    {
        foreach (var title in TitleCorpus.All)
        {
            new ComparableTitle(title).Key.ShouldBe(TitleNormalizer.Normalize(title));
        }
    }

    [Fact]
    public void A_title_compared_with_many_scores_the_same_with_each_as_it_does_alone()
    {
        var comparable = TitleSimilarityGrid.Titles.Select(title => (Title: title, Made: new ComparableTitle(title))).ToList();

        // Every title is compared again and again, in both positions: nothing a comparison works out may change the next one.
        var wrong = new List<string>();
        foreach (var (a, madeA) in comparable)
        {
            foreach (var (b, madeB) in comparable)
            {
                if (madeA.Similarity(madeB) != TitleNormalizer.Similarity(a, b) || madeA.TitleSimilarity(madeB) != TitleNormalizer.TitleSimilarity(a, b))
                {
                    wrong.Add($"'{a}' against '{b}'");
                }
            }
        }

        wrong.ShouldBeEmpty();
    }

    [Fact]
    public void A_blank_title_has_no_words_and_scores_nothing_against_any_title()
    {
        var blank = new ComparableTitle("  ");
        var title = new ComparableTitle("The Matrix");

        blank.Key.ShouldBe(string.Empty);
        blank.Similarity(title).ShouldBe(0.0);
        title.TitleSimilarity(blank).ShouldBe(0.0);
        new ComparableTitle(null).Similarity(blank).ShouldBe(0.0);
    }
}
