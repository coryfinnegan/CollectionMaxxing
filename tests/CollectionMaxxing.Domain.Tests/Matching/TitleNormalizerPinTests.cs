using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// The matcher decides what the scanner recognizes and is measured by the eval set, so how it computes a title key or a
// similarity may change, but not one key and not one bit of a score. These pin the keys and scores for a broad corpus
// (see TitleNormalizerPins for how the values are kept).
public class TitleNormalizerPinTests
{
    [Fact]
    public void The_pins_cover_every_title_of_the_corpus_in_order()
    {
        TitleNormalizerPins.Normalized.Select(pin => pin.Title).ShouldBe(TitleCorpus.All);
        TitleNormalizerPins.NormalizedLong.Select(pin => pin.Name).ShouldBe(TitleCorpus.Long.Select(title => title.Name));
    }

    [Fact]
    public void Every_corpus_title_normalizes_to_its_pinned_key()
    {
        var wrong = TitleNormalizerPins.Normalized
            .Select(pin => (pin.Title, Pinned: pin.Normalized, Actual: TitleNormalizer.Normalize(pin.Title)))
            .Where(row => row.Actual != row.Pinned)
            .Select(row => $"'{row.Title}' gives '{row.Actual}', pinned '{row.Pinned}'")
            .ToList();

        wrong.ShouldBeEmpty();
    }

    [Fact]
    public void Every_long_corpus_title_normalizes_to_its_pinned_key()
    {
        var wrong = TitleCorpus.Long
            .Zip(TitleNormalizerPins.NormalizedLong, (title, pin) => (title.Name, Pinned: pin.Digest, Actual: Digest(TitleNormalizer.Normalize(title.Title))))
            .Where(row => row.Actual != row.Pinned)
            .Select(row => $"{row.Name} gives {row.Actual}, pinned {row.Pinned}")
            .ToList();

        wrong.ShouldBeEmpty();
    }

    [Fact]
    public void Pinned_pairs_score_exactly_as_pinned_in_both_comparisons()
    {
        var wrong = new List<string>();
        foreach (var (a, b, similarity, titleSimilarity) in TitleNormalizerPins.Similarities)
        {
            var actualSimilarity = TitleNormalizer.Similarity(a, b);
            var actualTitleSimilarity = TitleNormalizer.TitleSimilarity(a, b);
            if (!SameBits(actualSimilarity, similarity) || !SameBits(actualTitleSimilarity, titleSimilarity))
            {
                wrong.Add($"'{a}' against '{b}': Similarity {Exact(actualSimilarity)} (pinned {Exact(similarity)}), TitleSimilarity {Exact(actualTitleSimilarity)} (pinned {Exact(titleSimilarity)})");
            }
        }

        wrong.ShouldBeEmpty();
    }

    [Fact]
    public void Every_ordered_pair_of_the_grid_scores_exactly_as_pinned()
    {
        TitleSimilarityGrid.Digest().ShouldBe(TitleNormalizerPins.GridDigest);
    }

    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static string Exact(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
