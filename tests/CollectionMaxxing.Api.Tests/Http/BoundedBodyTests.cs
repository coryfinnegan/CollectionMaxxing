using System.Text;
using CollectionMaxxing.Api.Http;

namespace CollectionMaxxing.Api.Tests.Http;

/// <summary>
/// The one request-body reader every endpoint uses: the exact bytes when the body fits, null when it doesn't, never more than one
/// byte read past the limit, and no buffer the size of the limit.
/// </summary>
public sealed class BoundedBodyTests
{
    private const int Limit = 1000;
    private const int Megabyte = 1024 * 1024;

    /// <summary>A body whose bytes never repeat at chunk boundaries, so a chunk copied twice or out of order is noticed.</summary>
    private static byte[] Body(int length)
    {
        var body = new byte[length];
        for (var i = 0; i < length; i++)
        {
            body[i] = (byte)((i * 31) + (i / 251) + 7);
        }

        return body;
    }

    private static ValueTask<byte[]?> ReadAsync(Stream stream, int limit, long? declared = null, CancellationToken cancellationToken = default) =>
        BoundedBody.ReadAsync(stream, limit, declared, cancellationToken);

    public static TheoryData<int, bool> FittingBodies()
    {
        var data = new TheoryData<int, bool>();
        foreach (var size in new[] { 1, 2, Limit - 1, Limit })
        {
            data.Add(size, false);
            data.Add(size, true);
        }

        return data;
    }

    [Fact]
    public async Task An_empty_body_is_an_empty_array_not_null()
    {
        (await ReadAsync(new MemoryStream(), Limit)).ShouldNotBeNull().ShouldBeEmpty();
        (await ReadAsync(new MemoryStream(), Limit, declared: 0)).ShouldNotBeNull().ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(FittingBodies))]
    public async Task A_body_up_to_the_limit_comes_back_exactly(int size, bool declared)
    {
        var body = Body(size);

        var read = await ReadAsync(new MemoryStream(body), Limit, declared ? size : null);

        read.ShouldBe(body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_byte_over_the_limit_is_too_large(bool declared)
    {
        var read = await ReadAsync(new MemoryStream(Body(Limit + 1)), Limit, declared ? Limit + 1 : null);

        read.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(10L)]
    [InlineData((long)Limit)]
    public async Task A_body_that_never_ends_costs_one_byte_past_the_limit(long? declared)
    {
        using var endless = new EndlessStream();

        var read = await ReadAsync(endless, Limit, declared);

        read.ShouldBeNull();
        endless.Consumed.ShouldBe(Limit + 1);
    }

    [Fact]
    public async Task A_limit_of_zero_takes_only_an_empty_body()
    {
        (await ReadAsync(new MemoryStream(), 0)).ShouldNotBeNull().ShouldBeEmpty();
        (await ReadAsync(new MemoryStream([1]), 0)).ShouldBeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task A_stream_that_gives_a_few_bytes_per_read_is_read_to_its_end(int bytesPerRead)
    {
        var body = Body(Limit);

        (await ReadAsync(new TrickleStream(body, bytesPerRead), Limit)).ShouldBe(body);
        (await ReadAsync(new TrickleStream(body, bytesPerRead), Limit, Limit)).ShouldBe(body);
        (await ReadAsync(new TrickleStream(Body(Limit + 1), bytesPerRead), Limit)).ShouldBeNull();
        (await ReadAsync(new TrickleStream(Body(Limit + 1), bytesPerRead), Limit, Limit)).ShouldBeNull();
    }

    [Theory]
    [InlineData(64 * 1024 - 1)]
    [InlineData(64 * 1024)]
    [InlineData(64 * 1024 + 1)]
    [InlineData(200_000)]
    [InlineData(Megabyte)]
    public async Task A_body_of_several_chunks_keeps_every_byte_in_order(int size)
    {
        var body = Body(size);

        (await ReadAsync(new MemoryStream(body), Megabyte)).ShouldBe(body);
        (await ReadAsync(new TrickleStream(body, 10_000), Megabyte)).ShouldBe(body);
        (await ReadAsync(new TrickleStream(body, 10_000), Megabyte, size)).ShouldBe(body);
    }

    [Fact]
    public async Task A_body_of_several_chunks_one_byte_over_the_limit_is_too_large()
    {
        var read = await ReadAsync(new TrickleStream(Body(Megabyte + 1), 10_000), Megabyte);

        read.ShouldBeNull();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(100L)]
    [InlineData(499L)]
    public async Task A_body_longer_than_its_declared_length_is_still_read_whole(long declared)
    {
        var body = Body(500);

        var read = await ReadAsync(new MemoryStream(body), Limit, declared);

        read.ShouldBe(body);
    }

    [Fact]
    public async Task A_body_longer_than_its_declared_length_and_over_the_limit_is_too_large()
    {
        using var stream = new TrickleStream(Body(Limit + 50), 64);

        var read = await ReadAsync(stream, Limit, declared: 10);

        read.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task A_body_shorter_than_its_declared_length_comes_back_as_it_is(int size)
    {
        var body = Body(size);

        var read = await ReadAsync(new MemoryStream(body), Limit, declared: 500);

        read.ShouldBe(body);
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_only_a_hint()
    {
        var body = Body(100);

        var read = await ReadAsync(new MemoryStream(body), Limit, declared: Limit * 1000L);

        read.ShouldBe(body);
    }

    [Fact]
    public async Task A_negative_declared_length_is_ignored()
    {
        var body = Body(100);

        var read = await ReadAsync(new MemoryStream(body), Limit, declared: -1);

        read.ShouldBe(body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(300L)]
    public async Task A_small_body_costs_about_its_own_size_not_the_size_of_the_limit(long? declared)
    {
        var body = Body(300);
        var limit = 4 * Megabyte;
        await ReadAsync(new MemoryStream(body), limit, declared); // the shared pool and the JIT are warm after this
        var stream = new MemoryStream(body);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = await ReadAsync(stream, limit, declared);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        read.ShouldBe(body);
        allocated.ShouldBeLessThan(100 * 1024);
    }

    [Fact]
    public async Task A_read_that_is_cancelled_stops_with_the_cancellation()
    {
        using var cancel = new CancellationTokenSource();
        using var stuck = new StuckStream();
        var pending = ReadAsync(stuck, Limit, cancellationToken: cancel.Token).AsTask();

        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_reads_nothing()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        using var stream = new TrickleStream(Body(100), 10);

        await Should.ThrowAsync<OperationCanceledException>(async () => await ReadAsync(stream, Limit, cancellationToken: cancel.Token));

        stream.Consumed.ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_stream_fails_the_read_and_not_the_pool()
    {
        await Should.ThrowAsync<IOException>(async () => await ReadAsync(new FailingStream(), Limit));

        var body = Body(Limit);
        (await ReadAsync(new MemoryStream(body), Limit)).ShouldBe(body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"x\":1}")]
    [InlineData("café ☃ \U0001F4DA")]
    public async Task Text_is_decoded_from_the_bytes_as_utf8(string text)
    {
        var read = await BoundedBody.ReadTextAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), Limit, CancellationToken.None);

        read.ShouldBe(text);
    }

    [Fact]
    public async Task Text_is_limited_in_bytes_not_in_characters()
    {
        var atTheLimit = new string('a', Limit);
        var twoBytesPerCharacter = new string('é', (Limit / 2) + 1);

        (await BoundedBody.ReadTextAsync(new MemoryStream(Encoding.UTF8.GetBytes(atTheLimit)), Limit, CancellationToken.None)).ShouldBe(atTheLimit);
        (await BoundedBody.ReadTextAsync(new MemoryStream(Encoding.UTF8.GetBytes(atTheLimit + "a")), Limit, CancellationToken.None)).ShouldBeNull();
        (await BoundedBody.ReadTextAsync(new MemoryStream(Encoding.UTF8.GetBytes(twoBytesPerCharacter)), Limit, CancellationToken.None)).ShouldBeNull();
    }

    /// <summary>Gives at most <c>perRead</c> bytes a call, however much room it is given, and counts what it handed out.</summary>
    private sealed class TrickleStream(byte[] body, int perRead) : ReadStream
    {
        private int position;

        protected override int Fill(Span<byte> destination)
        {
            var count = Math.Min(Math.Min(destination.Length, perRead), body.Length - position);
            body.AsSpan(position, count).CopyTo(destination);
            position += count;
            Consumed += count;
            return count;
        }
    }

    /// <summary>Never ends: zeros for as long as it is asked.</summary>
    private sealed class EndlessStream : ReadStream
    {
        protected override int Fill(Span<byte> destination)
        {
            destination.Clear();
            Consumed += destination.Length;
            return destination.Length;
        }
    }

    /// <summary>A connection that goes quiet: a read waits until it is cancelled.</summary>
    private sealed class StuckStream : ReadStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var cancelled = new TaskCompletionSource();
            await using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            await cancelled.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }

        protected override int Fill(Span<byte> destination) => throw new NotSupportedException();
    }

    /// <summary>A client that drops the connection mid-body.</summary>
    private sealed class FailingStream : ReadStream
    {
        protected override int Fill(Span<byte> destination) => throw new IOException("The client went away.");
    }

    /// <summary>The members of a forward-only read stream a test double never needs, with one method left to fill the buffer.</summary>
    private abstract class ReadStream : Stream
    {
        /// <summary>How many bytes the stream has handed out.</summary>
        public int Consumed { get; protected set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => Fill(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Fill(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected abstract int Fill(Span<byte> destination);
    }
}
