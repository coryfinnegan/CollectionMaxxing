using CollectionMaxxing.Infrastructure.Catalog;

namespace CollectionMaxxing.Infrastructure.Tests.Catalog;

public sealed class FileCoverStoreTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "cm-store-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public FileCoverStoreTests()
    {
        _root = Path.Combine(_parent, "covers");
        Directory.CreateDirectory(Path.Combine(_root, "ab"));
        File.WriteAllText(Path.Combine(_root, "ab", "x.png"), "inside");
        File.WriteAllText(Path.Combine(_parent, "secret.txt"), "outside");
    }

    public void Dispose() => Directory.Delete(_parent, recursive: true);

    [Fact]
    public async Task A_file_inside_the_root_is_opened()
    {
        await using var stream = await new FileCoverStore(_root).OpenReadAsync("ab/x.png", CancellationToken.None);

        stream.ShouldNotBeNull();
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).ShouldBe("inside");
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("ab/../../secret.txt")]
    [InlineData("ab/missing.png")]
    [InlineData("missing/x.png")]
    public async Task A_missing_or_escaping_path_gives_null(string path) =>
        (await new FileCoverStore(_root).OpenReadAsync(path, CancellationToken.None)).ShouldBeNull();
}
