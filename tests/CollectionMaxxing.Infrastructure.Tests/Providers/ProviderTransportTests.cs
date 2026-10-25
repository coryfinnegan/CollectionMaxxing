using System.Net;
using CollectionMaxxing.Infrastructure.Providers;
using CollectionMaxxing.Infrastructure.Providers.Quota;
using CollectionMaxxing.Infrastructure.Providers.UpcItemDb;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>
/// Issue #285: the provider HTTP transport. An oversized answer is refused while it is being read, never buffered in
/// full; every retry spends its own rate-limiter slot and quota token; concurrent identical lookups share one call.
/// </summary>
public class ProviderTransportTests(PostgresFixture db) : DatabaseTest(db)
{
    private const int TenMegabytes = 10 * 1024 * 1024;

    [Fact]
    public async Task Refuses_an_oversized_answer_from_the_headers_without_reading_its_body()
    {
        using var host = ProviderHost.Create(Db);
        var meter = new BodyMeter();
        var stub = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MeteredStream(meter, TenMegabytes, seekable: true)),
        });
        using var http = new HttpClient(stub);
        var provider = new UpcItemDbProvider(http, host.Get<IOptions<UpcItemDbOptions>>(), host.Get<ProviderRuntime>(), host.Get<ProviderQuotaStore>());

        var result = await provider.LookupWithStatusAsync(ProviderCase.Upc, default);

        result.Status.ShouldBe(UpcLookupStatus.Unavailable);
        meter.BytesRead.ShouldBe(0); // the Content-Length alone refused it: not one body byte was pulled
    }

    [Fact]
    public async Task Stops_reading_a_chunked_answer_once_it_passes_the_size_cap()
    {
        using var host = ProviderHost.Create(Db);
        var meter = new BodyMeter();
        var stub = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MeteredStream(meter, TenMegabytes, seekable: false)),
        });
        using var http = new HttpClient(stub);
        var provider = new UpcItemDbProvider(http, host.Get<IOptions<UpcItemDbOptions>>(), host.Get<ProviderRuntime>(), host.Get<ProviderQuotaStore>());

        var result = await provider.LookupWithStatusAsync(ProviderCase.Upc, default);

        result.Status.ShouldBe(UpcLookupStatus.Unavailable);
        meter.BytesRead.ShouldBeLessThanOrEqualTo(HttpCatalogProvider.MaxBodyBytes + 64 * 1024);

        await provider.LookupWithStatusAsync(ProviderCase.Upc, default);
        stub.Sends.ShouldBe(2); // the oversized body was not cached: the second ask went out again
    }

    [Fact]
    public async Task Counts_each_retry_against_the_daily_quota()
    {
        using var host = ProviderHost.Create(Db);
        var provider = ProviderCase.Named("upcitemdb");
        provider.FailOnceThenSucceed(host, 500);

        (await provider.Ask(host)).ShouldBeGreaterThan(0);

        host.Requests(provider.Path).ShouldBe(2);
        await using var context = Db.CreateContext();
        (await context.ProviderQuotas.SingleAsync()).Used.ShouldBe(2);
    }

    [Fact]
    public async Task A_retry_waits_for_its_own_rate_limiter_slot()
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:UpcItemDb:RequestsPerSecond"] = "1" });
        var provider = ProviderCase.Named("upcitemdb");
        provider.FailOnceThenSucceed(host, 500);

        var started = DateTimeOffset.UtcNow;
        (await provider.Ask(host)).ShouldBeGreaterThan(0);

        (DateTimeOffset.UtcNow - started).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
        host.Requests(provider.Path).ShouldBe(2);
    }

    [Fact]
    public async Task Refuses_a_retry_when_the_daily_quota_is_spent()
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:UpcItemDb:DailyLimit"] = "1" });
        var provider = ProviderCase.Named("upcitemdb");
        provider.RespondWith(host, 503, null);

        var result = await host.Get<UpcItemDbProvider>().LookupWithStatusAsync(ProviderCase.Upc, default);

        result.Status.ShouldBe(UpcLookupStatus.QuotaExhausted);
        host.Requests(provider.Path).ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Runs_concurrent_identical_lookups_as_one_upstream_call(string name)
    {
        using var host = ProviderHost.Create(Db);
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 200, provider.FoundFixture, delay: TimeSpan.FromMilliseconds(300));

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.Ask(host)));

        results.ShouldAllBe(count => count > 0);
        host.Requests(provider.Path).ShouldBe(1);
    }

    /// <summary>How much of a stubbed body was actually pulled.</summary>
    private sealed class BodyMeter
    {
        public long BytesRead;
    }

    /// <summary>
    /// A fixed body of zeros. Seekable, it advertises its length like an ordinary response; unseekable, it hides it
    /// like a chunked one. Every byte pulled passes through the <see cref="BodyMeter"/>.
    /// </summary>
    private sealed class MeteredStream(BodyMeter meter, long length, bool seekable) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => seekable;

        public override bool CanWrite => false;

        public override long Length => seekable ? length : throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => _position = seekable ? value : throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var left = length - _position;
            if (left <= 0)
            {
                return 0;
            }

            var n = (int)Math.Min(count, left);
            Array.Clear(buffer, offset, n);
            _position += n;
            Interlocked.Add(ref meter.BytesRead, n);
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Sends;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Sends);
            return Task.FromResult(respond());
        }
    }
}
