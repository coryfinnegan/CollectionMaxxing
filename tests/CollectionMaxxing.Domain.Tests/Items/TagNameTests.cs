using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Domain.Tests.Items;

public class TagNameTests
{
    [Fact]
    public void A_name_with_a_control_character_among_normal_ones_is_invalid()
    {
        var result = Tag.ValidateName("ab\u0001cd");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("tag.name_invalid");
    }

    [Fact]
    public void A_name_of_exactly_the_maximum_length_is_accepted_and_one_more_is_not()
    {
        Tag.ValidateName(new string('a', Tag.MaxNameLength)).IsSuccess.ShouldBeTrue();
        Tag.ValidateName(new string('a', Tag.MaxNameLength + 1)).Error!.Code.ShouldBe("tag.name_too_long");
    }

    [Fact]
    public void A_plain_name_is_trimmed_and_accepted()
    {
        var result = Tag.ValidateName("  signed  ");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("signed");
    }
}
