using CollectionMaxxing.Infrastructure.Ids;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Ids;

public class Uuid7IdGeneratorTests
{
    [Fact]
    public void Ids_are_version_7_uuids()
    {
        var generator = new Uuid7IdGenerator(new FakeTimeProvider());

        generator.NewId().Version.ShouldBe(7);
    }

    [Fact]
    public void Ids_sort_in_creation_order_when_time_moves_forward()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var generator = new Uuid7IdGenerator(clock);

        var first = generator.NewId();
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = generator.NewId();

        second.CompareTo(first).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Ids_embed_the_clock_time()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

        var id = new Uuid7IdGenerator(clock).NewId();

        // The first 48 bits of a UUIDv7 are the Unix time in milliseconds.
        var bytes = id.ToByteArray(bigEndian: true);
        var milliseconds = 0L;
        for (var i = 0; i < 6; i++)
        {
            milliseconds = (milliseconds << 8) | bytes[i];
        }

        milliseconds.ShouldBe(clock.GetUtcNow().ToUnixTimeMilliseconds());
    }
}
