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
    public void A_plain_name_is_trimmed_and_accepted()
    {
        var result = Tag.ValidateName("  signed  ");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("signed");
    }
}
