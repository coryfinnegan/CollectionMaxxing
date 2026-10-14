using CollectionMaxxing.Application.Catalog;

namespace CollectionMaxxing.Application.Tests.Catalog;

public class BlobPathsTests
{
    [Theory]
    [InlineData("ab/cd/ef01.jpg")]
    [InlineData("cover-1_a.webp")]
    public void A_plain_relative_path_is_safe(string path) => BlobPaths.IsSafe(path).ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("../x.png")]
    [InlineData("a/../../x.png")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/x.png")]
    [InlineData("a\\b.png")]
    [InlineData("a//b.png")]
    [InlineData("./a.png")]
    [InlineData("a/b.png\0")]
    public void A_rooted_traversing_or_odd_path_is_not_safe(string path) => BlobPaths.IsSafe(path).ShouldBeFalse();
}
