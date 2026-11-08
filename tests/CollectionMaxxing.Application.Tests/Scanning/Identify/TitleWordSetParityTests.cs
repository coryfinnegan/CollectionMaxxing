using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// The scorer's creator-line rule uses the Domain's word rule; it must stay the one PrintSupport.TitleWords defines.
public class TitleWordSetParityTests
{
    [Theory]
    [InlineData("The Lord of the Rings")]
    [InlineData("A")]
    [InlineData("It Follows")]
    [InlineData("Spider-Man: Homecoming")]
    [InlineData("FREE WILLY 3")]
    [InlineData("Up in the Air, or Is It?")]
    [InlineData("Amélie à la Française")]
    [InlineData("Se7en  1408")]
    [InlineData("of and to in on for at by with from or is an a the")]
    [InlineData("")]
    [InlineData(null)]
    public void The_domain_title_words_are_the_words_PrintSupport_gives(string? text)
    {
        TitleWordSet.Of(text).OrderBy(w => w, StringComparer.Ordinal)
            .ShouldBe(PrintSupport.TitleWords(text).Distinct().OrderBy(w => w, StringComparer.Ordinal));
    }
}
