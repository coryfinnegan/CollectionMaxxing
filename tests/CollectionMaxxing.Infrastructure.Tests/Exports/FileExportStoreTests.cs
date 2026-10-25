using CollectionMaxxing.Infrastructure.Exports;

namespace CollectionMaxxing.Infrastructure.Tests.Exports;

public sealed class FileExportStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-export-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_cleanup_failure_does_not_hide_the_write_error()
    {
        var store = new FileExportStore(_root);
        var exportId = Guid.CreateVersion7();

        var thrown = await Should.ThrowAsync<InvalidOperationException>(store.SaveAsync(exportId, Now, (stream, _) =>
        {
            stream.Dispose(); // so the temp file below can be replaced
            var temporary = Directory.GetFiles(_root, "*.partial").ShouldHaveSingleItem();
            File.Delete(temporary);
            Directory.CreateDirectory(temporary); // File.Delete on a directory throws: the cleanup now fails too
            throw new InvalidOperationException("the write failed");
        }, CancellationToken.None));

        thrown.Message.ShouldBe("the write failed");
    }

    [Fact]
    public async Task A_failed_write_still_removes_its_temp_file()
    {
        var store = new FileExportStore(_root);
        var exportId = Guid.CreateVersion7();

        await Should.ThrowAsync<InvalidOperationException>(store.SaveAsync(exportId, Now, (_, _) =>
            Task.FromException(new InvalidOperationException("boom")), CancellationToken.None));

        Directory.GetFileSystemEntries(_root).ShouldBeEmpty();
    }
}
