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
    private readonly SyntheticGolden _golden = new();
    private readonly FakeTimeProvider _clock = new();

    public void Dispose() => _golden.Dispose();

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

    private sealed class Refusing : IShelfStillMaker
    {
        public byte[]? Make(byte[] picture) => null;
    }

    private static ShelfReadResult Ok() => new([new ShelfReadItem("Quiet Harbor Atlas", "A. Example", ShelfItemKind.Book)], ShelfReadStatus.Ok);

    private ServerReadings Readings(IShelfReader reader, ShelfReaderOptions? options = null, bool enabled = true, IShelfStillMaker? stills = null) => new(
        new FileGoldenSet(Options.Create(new EvalsOptions { Enabled = enabled, GoldenPath = _golden.Golden, ResultsPath = _golden.Results })),
        reader,
        stills ?? new PassThrough(),
        Options.Create(options ?? new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "example-vl" }),
        Options.Create(new EvalsOptions { Enabled = enabled, GoldenPath = _golden.Golden, ResultsPath = _golden.Results }),
        _clock,
        NullLogger<ServerReadings>.Instance);

    private string CacheFolder => Path.Combine(_golden.Results, "reader-cache");

    private IReadOnlyList<string> CacheFiles() => Directory.Exists(CacheFolder) ? [.. Directory.GetFiles(CacheFolder)] : [];

    [Fact]
    public async Task A_photo_is_read_once_and_the_second_ask_is_answered_from_disk_with_the_time_the_read_took()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok, _clock, TimeSpan.FromSeconds(7));
        var readings = Readings(reader);

        var first = await readings.ReadAsync("c1", CancellationToken.None);
        var second = await readings.ReadAsync("c1", CancellationToken.None);

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

        await Readings(reader).ReadAsync("c1", CancellationToken.None);
        await Readings(reader).ReadAsync("c1", CancellationToken.None);
        reader.Calls.ShouldBe(1, "the same settings");

        await Readings(reader, new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "another-vl" }).ReadAsync("c1", CancellationToken.None);
        await Readings(reader, new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "example-vl", Prompt = "List what you see." }).ReadAsync("c1", CancellationToken.None);
        await Readings(reader, new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "example-vl", MaxTokens = 100 }).ReadAsync("c1", CancellationToken.None);
        await Readings(reader, new ShelfReaderOptions { Endpoint = "http://127.0.0.1:1", Model = "example-vl", MaxLongEdge = 512 }).ReadAsync("c1", CancellationToken.None);

        reader.Calls.ShouldBe(5);
        CacheFiles().Count.ShouldBe(5);
    }

    [Fact]
    public async Task New_picture_bytes_read_again()
    {
        var path = _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);
        var readings = Readings(reader);

        await readings.ReadAsync("c1", CancellationToken.None);
        await File.WriteAllBytesAsync(path, JpegFixtures.WithSize(3, 3));
        await readings.ReadAsync("c1", CancellationToken.None);

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

        var first = await readings.ReadAsync("c1", CancellationToken.None);
        await readings.ReadAsync("c1", CancellationToken.None);

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

        await readings.ReadAsync("c1", CancellationToken.None);
        var second = await readings.ReadAsync("c1", CancellationToken.None);

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
        await readings.ReadAsync("c1", CancellationToken.None);
        await File.WriteAllTextAsync(CacheFiles().Single(), "{ not json");

        var again = await readings.ReadAsync("c1", CancellationToken.None);

        reader.Calls.ShouldBe(2);
        again!.Result.Items.Count.ShouldBe(1);
        await readings.ReadAsync("c1", CancellationToken.None);
        reader.Calls.ShouldBe(2, "the file was replaced with a good one");
    }

    [Fact]
    public async Task Nothing_is_read_when_the_reader_is_off_the_evals_are_off_or_the_photo_is_not_listed()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);

        (await Readings(reader, new ShelfReaderOptions { Endpoint = "" }).ReadAsync("c1", CancellationToken.None)).ShouldBeNull();
        (await Readings(reader, enabled: false).ReadAsync("c1", CancellationToken.None)).ShouldBeNull();
        (await Readings(reader).ReadAsync("nope", CancellationToken.None)).ShouldBeNull();

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

        var result = await Readings(reader, stills: refuses).ReadAsync("c1", CancellationToken.None);

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

        await Task.WhenAll(readings.ReadAsync("c1", CancellationToken.None), readings.ReadAsync("c2", CancellationToken.None));

        reader.Calls.ShouldBe(2);
        reader.MostAtOnce.ShouldBe(1);
    }

    [Fact]
    public async Task A_cancelled_run_stops_cleanly_and_keeps_nothing()
    {
        _golden.AddSingle("c1", null);
        var reader = new CountingReader(Ok);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Readings(reader).ReadAsync("c1", cancelled.Token));

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
