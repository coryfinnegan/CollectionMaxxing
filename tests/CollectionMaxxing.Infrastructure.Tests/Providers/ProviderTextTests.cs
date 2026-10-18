using CollectionMaxxing.Infrastructure.Providers;
using Shouldly;
using System.Text.Json;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>What the forgiving JSON readers hand on: text is capped, and markup is only ever text (it is never interpreted here).</summary>
public class ProviderTextTests
{
    [Fact]
    public void A_huge_string_is_cut_to_a_sane_length()
    {
        using var document = JsonDocument.Parse("{\"title\":\"" + new string('A', 5_000_000) + "\",\"list\":[\"" + new string('B', 5_000_000) + "\"]}");

        document.RootElement.Str("title")!.Length.ShouldBe(ProviderJson.MaxTextLength);
        document.RootElement.Strings("list").Single().Length.ShouldBe(ProviderJson.MaxTextLength);
    }

    [Fact]
    public void Markup_in_a_string_is_returned_as_the_same_text_for_the_app_to_show_as_text()
    {
        using var document = JsonDocument.Parse("{\"title\":\"<img src=x onerror=alert(1)> Dune\"}");

        document.RootElement.Str("title").ShouldBe("<img src=x onerror=alert(1)> Dune");
    }

    [Fact]
    public void A_string_at_the_limit_is_kept_whole_and_short_ones_are_trimmed_as_before()
    {
        using var document = JsonDocument.Parse("{\"a\":\"" + new string('x', ProviderJson.MaxTextLength) + "\",\"b\":\"  Dune \"}");

        document.RootElement.Str("a")!.Length.ShouldBe(ProviderJson.MaxTextLength);
        document.RootElement.Str("b").ShouldBe("Dune");
    }
}
