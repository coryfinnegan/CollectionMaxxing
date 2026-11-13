using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.Infrastructure.ShelfReading;
using CollectionMaxxing.TestSupport;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Evals;

/// <summary>
/// The server reading a golden photo for the eval rows: the picture goes through the still maker and the reader, a photo already read with the same
/// settings is answered from disk, and a failed read is never kept. A fake reader and pictures drawn for the test; no model, no network.
/// </summary>
public sealed class ServerReadingsTests : IDisposable
{
    /// <summary>The longest any read may take here: a turn that is never given back fails the test instead of hanging it.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly SyntheticGolden _golden = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly ShelfReadGate _gate = new(Options.Create(new ShelfReadQueueOptions { QueueWaitSeconds = 1, QueueMaxWaiting = 0 }));

    /// <summary>
    /// Cancels every read still waiting once the test has had its time. A timed-out wait alone is not enough: the stuck read would keep
    /// holding the readings' one-at-a-time lock (static, shared by every test), and each later test would then wait out its own timeout.
    /// </summary>
    private readonly CancellationTokenSource _patience = new(Patience);

    public void Dispose()
    {
        _patience.Cancel();
        _patience.Dispose();
        _gate.Dispose();
        _golden.Dispose();
    }

    private sealed class CountingReader(Func<ShelfReadResult> answer, FakeTimeProvider? clock = null, TimeSpan? takes = null) : IShelfReader
    {
        public int Calls { get; private set; }

        public int Running { get; private set; }

        public int MostAtOnce { get; private set; }

        public async Task<ShelfReadResult> ReadAsync(byte[] jpeg, CancellationToken cancellationToken)
        {
            Calls++;
            Running++;
            MostAtOnce = Math.Max(MostAtOnce, Running);
            await Task.Delay(10, cancellationToken);
            clock?.Advance(takes ?? TimeSpan.Zero);
            Running--;
            return answer();
        }
    }

    private sealed class PassThrough : IShelfStillMaker
    {
        public byte[]? Make(byte[] picture) => picture;
    }

    private sealed class CountingStills : IShelfStillMaker
    {
        public int Calls { get; private set; }

        public byte[]? Make(byte[] picture)
        {
            Calls++;
            return picture;
        }
    }

    private sealed class Refusing : IShelfStillMaker
    {
        public byte[]? Make(byte[] picture) => null;
    }

    private static ShelfReadResult Ok() => new([new ShelfReadItem("Quiet Harbor Atlas", "A. Example", ShelfItemKind.Book)], ShelfReadStatus.Ok);

    private ServerReadings Readings(IShelfReader reader, ShelfReaderOptions? options = null, bool enabled = true, IShelfStillMaker? stills = null, int version = ServerReadings.ReaderVersion) => new(
        new FileGoldenSet(Options.Create(new EvalsOptions { Enabled = enabled, GoldenPath = _golden.Golden, ResultsPath = _golden.Results })),
        reader,
        stills ?? new PassThrough(),
        _gate,
        Options.Create(options ?? new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "example-vl" }),
        Options.Create(new EvalsOptions { Enabled = enabled, GoldenPath = _golden.Golden, ResultsPath = _golden.Results }),
        _clock,
        NullLogger<ServerReadings>.Instance,
        version);

    private string CacheFolder => Path.Combine(_golden.Results, "reader-cache");

    private IReadOnlyList<string> CacheFiles() => Directory.Exists(CacheFolder) ? [.. Directory.GetFiles(CacheFolder)] : [];

    [Fact]
    public async Task A_photo_is_read_once_and_the_second_ask_is_answered_from_disk_with_the_time_the_read_took()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok, _clock, TimeSpan.FromSeconds(7));
        var readings = Readings(reader);

        var first = await readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        var second = await readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);

        reader.Calls.ShouldBe(1);
        first!.LatencyMs.ShouldBe(7000);
        second!.LatencyMs.ShouldBe(7000);
        second.Result.Items.ShouldBe(first.Result.Items);
        CacheFiles().Count.ShouldBe(1);
        Path.GetFileName(CacheFiles()[0]).ShouldMatch("^[0-9a-f]{64}-[0-9a-f]{64}\\.json$");
    }

    [Fact]
    public async Task A_new_process_with_the_same_settings_finds_the_cache_a_new_model_prompt_or_setting_reads_again()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);

        await Readings(reader).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        await Readings(reader).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        reader.Calls.ShouldBe(1, "the same settings");

        await Readings(reader, new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "another-vl" }).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        await Readings(reader, new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "example-vl", Prompt = "List what you see." }).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        await Readings(reader, new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "example-vl", MaxTokens = 100 }).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        await Readings(reader, new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "example-vl", MaxLongEdge = 512 }).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        await Readings(reader, new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "example-vl", SecondLook = false }).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        await Readings(reader, new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "example-vl", SecondLookMaxTokens = 450 }).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);

        reader.Calls.ShouldBe(7);
        CacheFiles().Count.ShouldBe(7);
    }

    [Fact]
    public void The_reader_version_was_bumped_for_the_second_look()
    {
        ServerReadings.ReaderVersion.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task A_different_reader_version_reads_again()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);

        await Readings(reader, version: 1).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        await Readings(reader, version: 1).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        reader.Calls.ShouldBe(1);
        await Readings(reader, version: 2).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);

        reader.Calls.ShouldBe(2);
        CacheFiles().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Cached_only_with_a_warm_cache_answers_without_calling_the_reader_or_the_still_maker()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);
        await Readings(reader).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        var stills = new CountingStills();

        var served = await Readings(reader, stills: stills).ReadAsync("c1", true, _patience.Token).WaitAsync(Patience);

        served!.Result.Items.Count.ShouldBe(1);
        reader.Calls.ShouldBe(1);
        stills.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Cached_only_with_a_cold_cache_gives_nothing_and_calls_neither_the_reader_nor_the_still_maker()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);
        var stills = new CountingStills();

        var served = await Readings(reader, stills: stills).ReadAsync("c1", true, _patience.Token).WaitAsync(Patience);

        served.ShouldBeNull();
        reader.Calls.ShouldBe(0);
        stills.Calls.ShouldBe(0);
        CacheFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task New_picture_bytes_read_again()
    {
        var path = _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);
        var readings = Readings(reader);

        await readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        await File.WriteAllBytesAsync(path, JpegFixtures.WithSize(3, 3));
        await readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);

        reader.Calls.ShouldBe(2);
        CacheFiles().Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(ShelfReadStatus.Unavailable)]
    [InlineData(ShelfReadStatus.TimedOut)]
    public async Task A_failed_read_is_given_back_but_never_kept(ShelfReadStatus status)
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(() => ShelfReadResult.Empty(status));
        var readings = Readings(reader);

        var first = await readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        await readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);

        first!.Result.Status.ShouldBe(status);
        reader.Calls.ShouldBe(2, "a failed read is tried again");
        CacheFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_cut_short_keeps_its_items_in_the_cache()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(() => new ShelfReadResult(Ok().Items, ShelfReadStatus.CutShort));
        var readings = Readings(reader);

        await readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        var second = await readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);

        reader.Calls.ShouldBe(1);
        second!.Result.Status.ShouldBe(ShelfReadStatus.CutShort);
        second.Result.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_cache_file_that_cannot_be_read_is_read_again_and_replaced()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);
        var readings = Readings(reader);
        await readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        await File.WriteAllTextAsync(CacheFiles().Single(), "{ not json");

        var again = await readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);

        reader.Calls.ShouldBe(2);
        again!.Result.Items.Count.ShouldBe(1);
        await readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        reader.Calls.ShouldBe(2, "the file was replaced with a good one");
    }

    [Fact]
    public async Task Nothing_is_read_when_the_reader_is_off_the_evals_are_off_or_the_photo_is_not_listed()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);

        (await Readings(reader, new ShelfReaderOptions { Endpoint = "" }).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience)).ShouldBeNull();
        (await Readings(reader, enabled: false).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience)).ShouldBeNull();
        (await Readings(reader).ReadAsync("nope", false, _patience.Token).WaitAsync(Patience)).ShouldBeNull();

        reader.Calls.ShouldBe(0);
        Readings(reader, new ShelfReaderOptions { Endpoint = "" }).IsConfigured.ShouldBeFalse();
        Readings(reader).IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task A_picture_the_still_maker_refuses_is_an_unavailable_read_that_is_not_kept()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);
        var refuses = new Refusing();

        var result = await Readings(reader, stills: refuses).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);

        result!.Result.Status.ShouldBe(ShelfReadStatus.Unavailable);
        reader.Calls.ShouldBe(0);
        CacheFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Reads_happen_one_at_a_time_even_when_asked_together()
    {
        _golden.AddSingle("c1", null);
        _golden.AddSingle("c2", null);
        var path = Path.Combine(_golden.Golden, "single", "c2.jpg");
        await File.WriteAllBytesAsync(path, JpegFixtures.WithSize(5, 5)); // a different picture, so the second ask is no cache hit
        var reader = new CountingReader(Ok);
        var readings = Readings(reader);

        await Task.WhenAll(readings.ReadAsync("c1", false, _patience.Token).WaitAsync(Patience), readings.ReadAsync("c2", false, _patience.Token).WaitAsync(Patience));

        reader.Calls.ShouldBe(2);
        reader.MostAtOnce.ShouldBe(1);
    }

    [Fact]
    public async Task A_read_waits_while_the_scanner_has_the_reader_and_reads_once_it_is_free()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);
        var scanner = await _gate.EnterAsync(CancellationToken.None); // a person's :read-shelf holds the one reader
        scanner.ShouldNotBeNull();

        var read = Readings(reader).ReadAsync("c1", false, _patience.Token).WaitAsync(Patience);
        await Task.Delay(1500); // longer than the queue lets a person wait: the re-ground is never told the reader is busy

        read.IsCompleted.ShouldBeFalse("it waits its turn");
        reader.Calls.ShouldBe(0);
        scanner.Dispose();
        var served = await read.WaitAsync(TimeSpan.FromSeconds(10));
        served.ShouldNotBeNull().Result.Status.ShouldBe(ShelfReadStatus.Ok);
        reader.Calls.ShouldBe(1);
        using var next = await _gate.EnterAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        next.ShouldNotBeNull("the read gave the turn back");
    }

    [Fact]
    public async Task A_cancelled_run_stops_cleanly_and_keeps_nothing()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Readings(reader).ReadAsync("c1", false, cancelled.Token));

        CacheFiles().ShouldBeEmpty();
    }

    [Fact]
    public void The_report_label_names_the_model_and_a_hash_of_the_prompt()
    {
        var one = Readings(new CountingReader(Ok)).ReaderLabel;
        var another = Readings(new CountingReader(Ok), new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "example-vl", Prompt = "Other." }).ReaderLabel;

        one.ShouldStartWith("model example-vl, prompt hash ");
        one.ShouldNotBe(another);
    }
}
