using CollectionMaxxing.Infrastructure.Accounts.Deletion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Deletion;

// LIB-2d (issue #99), item 2: one file that can't be deleted must not fail a person's whole purge, now and on every retry.
public sealed class FileAccountFilesTests : IDisposable
{
    private const string BlobPath = "ab/cd/0123456789abcdef0123456789abcdef.jpg";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-files-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    private string PathOnDisk => Path.Combine(_root, "ab", "cd", "0123456789abcdef0123456789abcdef.jpg");

    /// <summary>Runs the delete while the fake clock moves on, so that the waits between its tries pass.</summary>
    private async Task DeleteAsync(FileAccountFiles files)
    {
        var deleting = files.DeleteAsync(BlobPath, CancellationToken.None);
        while (!deleting.IsCompleted)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        await deleting;
    }

    [Fact]
    public async Task A_file_that_is_already_gone_is_fine()
    {
        Directory.CreateDirectory(_root);

        await DeleteAsync(new FileAccountFiles(_root, _time, NullLogger.Instance));

        Directory.Exists(Path.Combine(_root, "ab")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_that_is_there_is_deleted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
        await File.WriteAllBytesAsync(PathOnDisk, [1, 2, 3]);

        await DeleteAsync(new FileAccountFiles(_root, _time, NullLogger.Instance));

        File.Exists(PathOnDisk).ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_is_tried_again_then_reported_and_the_purge_carries_on()
    {
        // A directory where the file should be: deleting it fails on every try (on every operating system), like a file that stays locked.
        Directory.CreateDirectory(PathOnDisk);
        await File.WriteAllBytesAsync(Path.Combine(PathOnDisk, "inside"), [1]);
        var logger = new RecordingLogger();

        await DeleteAsync(new FileAccountFiles(_root, _time, logger)); // must not throw

        logger.Levels.ShouldContain(LogLevel.Warning, "the stuck file is reported");
        Directory.Exists(PathOnDisk).ShouldBeTrue("it was not touched");
    }
}
