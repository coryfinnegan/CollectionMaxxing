using CollectionMaxxing.Infrastructure.Providers;
using CollectionMaxxing.Infrastructure.Providers.Resilience;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

public class TokenBucketRateLimiterTests
{
    [Fact]
    public void Ten_rapid_calls_to_a_3_per_second_provider_are_spread_over_at_least_three_seconds()
    {
        var clock = new FakeTimeProvider();
        var limiter = TokenBucketRateLimiter.PerSecond(3, clock);

        var startTimes = Enumerable.Range(0, 10).Select(_ => clock.GetUtcNow() + limiter.Reserve()).ToList();

        (startTimes[^1] - startTimes[0]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(3));
        for (var i = 3; i < startTimes.Count; i++)
        {
            // Never more than 3 calls in any one second: the fourth is at least a second after the first.
            (startTimes[i] - startTimes[i - 3]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(10));
        }
    }

    [Fact]
    public void A_call_after_a_quiet_spell_goes_straight_through()
    {
        var clock = new FakeTimeProvider();
        var limiter = TokenBucketRateLimiter.PerSecond(3, clock);
        limiter.Reserve();

        clock.Advance(TimeSpan.FromSeconds(5));

        limiter.Reserve().ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task A_waiting_call_is_released_only_when_the_clock_reaches_its_slot()
    {
        var clock = new FakeTimeProvider();
        var limiter = TokenBucketRateLimiter.PerSecond(1, clock);
        await limiter.WaitAsync(default); // takes the first slot at once

        var second = limiter.WaitAsync(default);
        second.IsCompleted.ShouldBeFalse();

        clock.Advance(TimeSpan.FromMilliseconds(999));
        second.IsCompleted.ShouldBeFalse();

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class CoverHostsTests
{
    [Theory]
    [InlineData("https://covers.openlibrary.org/b/id/1-L.jpg", "https://covers.openlibrary.org/b/id/1-L.jpg")]
    [InlineData("http://books.google.com/books/content?id=x&zoom=1", "https://books.google.com/books/content?id=x&zoom=1")]
    [InlineData("https://image.tmdb.org/t/p/w500/a.jpg", "https://image.tmdb.org/t/p/w500/a.jpg")]
    [InlineData("https://images.igdb.com/igdb/image/upload/t_cover_big/co1.jpg", "https://images.igdb.com/igdb/image/upload/t_cover_big/co1.jpg")]
    [InlineData("https://IMAGES.IGDB.COM/x.jpg", "https://images.igdb.com/x.jpg")]
    public void Keeps_covers_on_allow_listed_hosts_over_https(string url, string expected) =>
        CoverHosts.Allow(url).ShouldBe(expected);

    [Theory]
    [InlineData("https://evil.example.net/cover.jpg")]
    [InlineData("https://covers.openlibrary.org.evil.example.net/a.jpg")]
    [InlineData("https://user:pass@covers.openlibrary.org/a.jpg")]
    [InlineData("ftp://covers.openlibrary.org/a.jpg")]
    [InlineData("//covers.openlibrary.org/a.jpg")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Drops_every_other_cover_url(string? url) => CoverHosts.Allow(url).ShouldBeNull();
}
