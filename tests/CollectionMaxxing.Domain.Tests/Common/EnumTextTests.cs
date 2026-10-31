using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Domain.Tests.Common;

public class EnumTextTests
{
    private static readonly MethodInfo ToTextMethod = typeof(EnumText).GetMethod(nameof(EnumText.ToText))!;
    private static readonly MethodInfo ParseMethod = typeof(EnumText).GetMethod(nameof(EnumText.Parse))!;
    private static readonly MethodInfo AllMethod = typeof(EnumText).GetMethod(nameof(EnumText.All))!;

    /// <summary>Every enum the domain declares, so a new one is covered the day it is added.</summary>
    public static TheoryData<Type> DomainEnums() => new(typeof(WorkKind).Assembly.GetTypes().Where(t => t.IsEnum));

    /// <summary>The spelling the class summary promises, worked out from the member itself instead of looked up.</summary>
    private static string Spelled(Type enumType, object member)
    {
        var name = member.ToString()!;
        return enumType.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
            ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
    }

    private static T Call<T>(MethodInfo method, Type enumType, params object?[] arguments) =>
        (T)method.MakeGenericMethod(enumType).Invoke(null, arguments)!;

    [Fact]
    public void There_are_domain_enums_to_check()
    {
        DomainEnums().Count.ShouldBeGreaterThan(10);
    }

    [Theory]
    [MemberData(nameof(DomainEnums))]
    public void Every_member_of_every_domain_enum_is_spelled_by_its_name_or_its_attribute(Type enumType)
    {
        foreach (var member in Enum.GetValues(enumType))
        {
            Call<string>(ToTextMethod, enumType, member).ShouldBe(Spelled(enumType, member));
        }
    }

    [Theory]
    [MemberData(nameof(DomainEnums))]
    public void Every_member_of_every_domain_enum_parses_back_from_its_spelling(Type enumType)
    {
        foreach (var member in Enum.GetValues(enumType))
        {
            Call<object>(ParseMethod, enumType, Spelled(enumType, member)).ShouldBe(member);
        }
    }

    [Theory]
    [MemberData(nameof(DomainEnums))]
    public void All_lists_the_spellings_in_declaration_order(Type enumType)
    {
        var expected = Enum.GetValues(enumType).Cast<object>().Select(member => Spelled(enumType, member));

        Call<IReadOnlyList<string>>(AllMethod, enumType).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(DomainEnums))]
    public void No_two_members_of_a_domain_enum_share_a_spelling(Type enumType)
    {
        var spellings = Call<IReadOnlyList<string>>(AllMethod, enumType);

        spellings.Distinct().Count().ShouldBe(spellings.Count);
    }

    [Fact]
    public void Spellings_are_snake_case_unless_the_member_says_otherwise()
    {
        EnumText.ToText(ItemCondition.VeryGood).ShouldBe("very_good");
        EnumText.ToText(ReleaseFormat.UhdBluray).ShouldBe("uhd_bluray");
        EnumText.ToText(ItemCompleteness.NotApplicable).ShouldBe("n_a");
        EnumText.ToText(WorkKind.Film).ShouldBe("film");
    }

    [Fact]
    public void A_value_that_is_not_a_declared_member_is_spelled_from_its_number()
    {
        EnumText.ToText((WorkKind)99).ShouldBe("99");
    }

    [Fact]
    public void Parse_gives_back_the_member_with_that_text()
    {
        EnumText.Parse<WorkKind>("film").ShouldBe(WorkKind.Film);
        EnumText.Parse<WorkKind>(EnumText.ToText(WorkKind.Other)).ShouldBe(WorkKind.Other);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("Film")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_of_an_unknown_text_throws(string? text)
    {
        var error = Should.Throw<ArgumentException>(() => EnumText.Parse<WorkKind>(text!));

        error.ParamName.ShouldBe("text");
        error.Message.ShouldStartWith($"'{text}' is not a WorkKind.");
    }

    [Fact]
    public void TryParse_gives_back_the_member_with_that_text()
    {
        EnumText.TryParse<ItemCompleteness>("n_a", out var value).ShouldBeTrue();

        value.ShouldBe(ItemCompleteness.NotApplicable);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("NotApplicable")]
    [InlineData("N_A")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_of_an_unknown_text_is_false(string? text)
    {
        EnumText.TryParse<ItemCompleteness>(text, out _).ShouldBeFalse();
    }

    [Fact]
    public void ParseOrNull_is_null_for_a_missing_or_unknown_text()
    {
        EnumText.ParseOrNull<WorkKind>("game").ShouldBe(WorkKind.Game);
        EnumText.ParseOrNull<WorkKind>("nonsense").ShouldBeNull();
        EnumText.ParseOrNull<WorkKind>(null).ShouldBeNull();
    }

    [Fact]
    public void Reading_and_writing_a_spelling_allocates_nothing_once_it_is_warm()
    {
        var kinds = Enum.GetValues<ReleaseFormat>();
        var texts = Array.ConvertAll(kinds, EnumText.ToText);
        var checksum = 0;
        for (var warmup = 0; warmup < 3; warmup++)
        {
            checksum += Roundtrip(kinds, texts);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 100; round++)
        {
            checksum += Roundtrip(kinds, texts);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
        checksum.ShouldBeGreaterThan(0);
    }

    private static int Roundtrip(ReleaseFormat[] kinds, string[] texts)
    {
        var sum = 0;
        for (var i = 0; i < kinds.Length; i++)
        {
            sum += EnumText.ToText(kinds[i]).Length;
            sum += (int)EnumText.Parse<ReleaseFormat>(texts[i]);
            sum += EnumText.TryParse<ReleaseFormat>(texts[i], out var parsed) ? (int)parsed : 0;
            sum += EnumText.ParseOrNull<ReleaseFormat>(texts[i]) is { } maybe ? (int)maybe : 0;
        }

        return sum;
    }
}
