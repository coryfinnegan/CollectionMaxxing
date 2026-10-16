using System.Text;
using CollectionMaxxing.Application.Recognition;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Recognition;

/// <summary>Where the optional override file is. Bound from the <c>Recognition</c> configuration section.</summary>
public sealed class RecognitionOptions
{
    public const string SectionName = "Recognition";

    /// <summary>
    /// The override file: JSON in the shape of <c>recognition.defaults.json</c> (any subset of it), plus an optional
    /// <c>scoring</c> section. Empty means no override. Never served or logged.
    /// </summary>
    public string? ConfigPath { get; set; }
}

/// <summary>
/// The live recognition config: the checked-in defaults with the override file merged over them, re-read when the file changes.
/// A file that fails validation is ignored with an error in the log and the last good config keeps serving, so a bad edit never
/// takes the API down and never serves partial values. A file that disappears or can't be read keeps the last good config too
/// (an editor may be saving it); to go back to the defaults, edit the file to <c>{}</c>.
/// </summary>
/// <remarks>
/// Each read checks the file's size and modified time (one cheap stat) and re-reads it only when they changed, so there is no
/// watcher to miss an event and nothing to restart. The file's location is never logged or served.
/// </remarks>
public sealed partial class RecognitionConfigStore : IRecognitionSettings
{
    /// <summary>A bigger file is refused unread: it is a mistake, not a tuning change.</summary>
    public const int MaxFileBytes = 64 * 1024;

    private const int MaxProblemsLogged = 10;
    private const int MaxProblemLength = 300;

    private static readonly FileStamp NotLookedAtYet = new(false, default, -1);
    private static readonly FileStamp Missing = new(false, default, 0);

    private static readonly TimeSpan RecentEdit = TimeSpan.FromSeconds(2);

    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger<RecognitionConfigStore> _logger;
    private readonly Lock _gate = new();

    private volatile Snapshot _current;
    private FileStamp _seen = NotLookedAtYet;
    private FileStamp _unreadableAt = NotLookedAtYet;
    private FileStamp _brokenAt = NotLookedAtYet;
    private string? _lastText;

    public RecognitionConfigStore(IOptions<RecognitionOptions> options, ILogger<RecognitionConfigStore> logger, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _path = string.IsNullOrWhiteSpace(options.Value.ConfigPath) ? null : options.Value.ConfigPath;

        // The checked-in defaults must be valid: if they are not, that is a bug in this build and starting is refused.
        var defaults = RecognitionConfigBuilder.Build(RecognitionDefaults.Json, overrideJson: null);
        _current = defaults.IsValid
            ? new Snapshot(defaults.Config!, defaults.Scoring!)
            : throw new InvalidOperationException("The checked-in recognition defaults are invalid: " + string.Join(' ', defaults.Problems));

        Refresh();
    }

    /// <inheritdoc />
    public RecognitionConfig Config => Refresh().Config;

    /// <inheritdoc />
    public ScoringOptions Scoring => Refresh().Scoring;

    /// <summary>
    /// Reads at most <paramref name="max"/> bytes of UTF-8 text from <paramref name="stream"/>, or returns null when there is
    /// more than that. The size check before opening can be out of date by the time the file is read, so this one is the cap.
    /// </summary>
    public static string? ReadBounded(Stream stream, int max)
    {
        var buffer = new byte[max + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
        {
            length += read;
        }

        if (length > max)
        {
            return null;
        }

        using var reader = new StreamReader(new MemoryStream(buffer, 0, length), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private Snapshot Refresh()
    {
        if (_path is null)
        {
            return _current;
        }

        var stamp = StampOf(_path);
        lock (_gate)
        {
            // A file edited in the last moments is read again even when its size and time look the same: two quick saves can
            // share a clock tick. The text is compared, so reading it again changes nothing unless it really changed.
            if (stamp != _seen || IsRecent(stamp))
            {
                try
                {
                    Load(stamp);
                }
                catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
                {
                    // Whatever went wrong, the config that is serving keeps serving. Type only: messages can quote the file.
                    LogBroken(e.GetType().Name);
                }
            }

            return _current;
        }
    }

    private bool IsRecent(FileStamp stamp)
    {
        var age = _time.GetUtcNow().UtcDateTime - stamp.LastWriteUtc;
        return stamp.Exists && age > -RecentEdit && age < RecentEdit;
    }

    private void Load(FileStamp stamp)
    {
        if (!stamp.Exists)
        {
            if (_seen.Exists)
            {
                LogGone();
            }

            _seen = stamp;
            return;
        }

        if (stamp.Length > MaxFileBytes)
        {
            LogTooBig(stamp.Length, MaxFileBytes);
            _seen = stamp;
            return;
        }

        string? text;
        try
        {
            using var stream = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            text = ReadBounded(stream, MaxFileBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not logged with the exception: its message carries the file's path. Looked at again on the next read.
            if (_unreadableAt != stamp)
            {
                LogUnreadable(e.GetType().Name);
                _unreadableAt = stamp;
            }

            return;
        }

        _seen = stamp;
        if (text is null)
        {
            // Under the cap when it was measured, over it when read: the file grew in between.
            LogTooBig(MaxFileBytes + 1L, MaxFileBytes);
            return;
        }

        if (text == _lastText)
        {
            return;
        }

        RecognitionBuild build;
        try
        {
            build = RecognitionConfigBuilder.Build(RecognitionDefaults.Json, text);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // Never the message (it can quote the file) and never the path: the type alone. Keep what is serving.
            if (_brokenAt != stamp)
            {
                LogBroken(e.GetType().Name);
                _brokenAt = stamp;
            }

            return;
        }

        _lastText = text;
        if (!build.IsValid)
        {
            var shown = string.Join(' ', build.Problems.Take(MaxProblemsLogged).Select(p => p.Length <= MaxProblemLength ? p : p[..MaxProblemLength] + "..."));
            LogRejected(build.Problems.Count, shown);
            return;
        }

        var config = build.Config!;
        _current = new Snapshot(config, build.Scoring!);
        LogReloaded(config.Version);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The recognition override file could not be applied ({Failure}); keeping the last good config.")]
    private partial void LogBroken(string failure);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The recognition override file is gone; keeping the last good config. Edit it to {{}} to go back to the defaults.")]
    private partial void LogGone();

    [LoggerMessage(Level = LogLevel.Error, Message = "The recognition override file is {Bytes} bytes; the most is {Max}. Ignoring it and keeping the last good config.")]
    private partial void LogTooBig(long bytes, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The recognition override file can't be read right now ({Failure}); keeping the last good config and trying again.")]
    private partial void LogUnreadable(string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "The recognition override file was rejected ({Count} problem(s)); keeping the last good config. {Problems}")]
    private partial void LogRejected(int count, string problems);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recognition config reloaded from the override file (version {Version}).")]
    private partial void LogReloaded(string version);

    private static FileStamp StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(true, info.LastWriteTimeUtc, info.Length) : Missing;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Missing;
        }
    }

    private sealed record Snapshot(RecognitionConfig Config, ScoringOptions Scoring);

    private readonly record struct FileStamp(bool Exists, DateTime LastWriteUtc, long Length);
}
