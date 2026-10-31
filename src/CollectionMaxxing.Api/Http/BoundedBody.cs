using System.Buffers;
using System.Text;

namespace CollectionMaxxing.Api.Http;

/// <summary>
/// Reads a request body into memory with a ceiling, for every endpoint that takes a body of its own. The answer is the exact body
/// in one array, or null when the body is longer than the limit; at most one byte past the limit is ever read. Nothing the size of the
/// limit is allocated up front: a body the caller declared the length of is read straight into an array of that size, any other in
/// pooled chunks that are copied once into an array of the length it turned out to be.
/// </summary>
public static class BoundedBody
{
    /// <summary>Under the large object heap threshold, so a chunk comes from the shared pool and goes back to it.</summary>
    private const int ChunkBytes = 64 * 1024;

    /// <summary>A body that ends where it said it would is found out by one read, which needs no more than this.</summary>
    private const int ProbeBytes = 16;

    /// <summary>
    /// The body, or null when it is longer than <paramref name="maxBytes"/>. A <paramref name="declaredLength"/> of 1 to
    /// <paramref name="maxBytes"/> sizes the answer once; it is only a hint, so a body that is shorter or longer than it said is still
    /// read as it is, and a declared length outside that range is not trusted to size anything.
    /// </summary>
    public static async ValueTask<byte[]?> ReadAsync(Stream stream, int maxBytes, long? declaredLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfEqual(maxBytes, int.MaxValue); // one more byte than the limit is always asked for

        byte[] head = [];
        if (declaredLength is long declared and > 0 && declared <= maxBytes)
        {
            head = GC.AllocateUninitializedArray<byte>((int)declared);
            var filled = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
            if (filled < head.Length)
            {
                return head.AsSpan(0, filled).ToArray();
            }
        }

        return await AppendRestAsync(stream, head, maxBytes, cancellationToken);
    }

    /// <summary>The body as UTF-8 text, or null when it is longer than <paramref name="maxBytes"/> bytes. Decoded from the exact bytes.</summary>
    public static async ValueTask<string?> ReadTextAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync(stream, maxBytes, declaredLength: null, cancellationToken);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// What the stream still holds after <paramref name="head"/> (which is already read and counts against the limit), joined onto it.
    /// Null when that makes more than <paramref name="maxBytes"/>. With a full <paramref name="head"/> this is the check that the body
    /// ended where it said it would, and it hands back <paramref name="head"/> itself.
    /// </summary>
    private static async ValueTask<byte[]?> AppendRestAsync(Stream stream, byte[] head, int maxBytes, CancellationToken cancellationToken)
    {
        var room = maxBytes - head.Length;
        List<byte[]>? full = null;
        var chunk = ArrayPool<byte>.Shared.Rent(Math.Min(room + 1, head.Length > 0 ? ProbeBytes : ChunkBytes));
        var used = 0;
        var total = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(used, Math.Min(chunk.Length - used, room + 1 - total)), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                used += read;
                if (total > room)
                {
                    return null;
                }

                if (used == chunk.Length)
                {
                    var next = ArrayPool<byte>.Shared.Rent(Math.Min(room + 1 - total, ChunkBytes));
                    (full ??= []).Add(chunk);
                    chunk = next;
                    used = 0;
                }
            }

            if (total == 0)
            {
                return head;
            }

            var body = GC.AllocateUninitializedArray<byte>(head.Length + total);
            head.CopyTo(body, 0);
            var written = head.Length;
            if (full is not null)
            {
                foreach (var filled in full)
                {
                    filled.CopyTo(body, written);
                    written += filled.Length;
                }
            }

            chunk.AsSpan(0, used).CopyTo(body.AsSpan(written));
            return body;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            if (full is not null)
            {
                foreach (var filled in full)
                {
                    ArrayPool<byte>.Shared.Return(filled);
                }
            }
        }
    }
}
