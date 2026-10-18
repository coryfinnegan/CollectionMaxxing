using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Tests.Common;

public class EnumTextTests
{
    [Fact]
    public void Parse_gives_back_the_member_with_that_text()
    {
        EnumText.Parse<WorkKind>("film").ShouldBe(WorkKind.Film);
        EnumText.Parse<WorkKind>(EnumText.ToText(WorkKind.Other)).ShouldBe(WorkKind.Other);
    }

    [Fact]
    public void Parse_of_an_unknown_text_throws()
    {
        Should.Throw<ArgumentException>(() => EnumText.Parse<WorkKind>("nonsense"));
    }
}
