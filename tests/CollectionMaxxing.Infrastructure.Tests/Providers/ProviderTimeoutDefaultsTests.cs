using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Infrastructure.Providers.Resilience;
using Shouldly;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>Open Library answers an ISBN in 4 to 17 seconds on a slow day; the defaults must wait long enough for that to find the book.</summary>
public class ProviderTimeoutDefaultsTests
{
    private static readonly TimeSpan SlowDay = TimeSpan.FromSeconds(17);

    [Fact]
    public void A_provider_attempt_waits_longer_than_a_slow_open_library_answer() =>
        new ProviderResilienceOptions().AttemptTimeout.ShouldBeGreaterThan(SlowDay);

    [Fact]
    public void The_whole_provider_call_outlasts_one_attempt() =>
        new ProviderResilienceOptions().TotalTimeout.ShouldBeGreaterThan(new ProviderResilienceOptions().AttemptTimeout);

    [Fact]
    public void A_catalogue_request_waits_longer_than_a_slow_open_library_answer() =>
        new CatalogOptions().ProviderTimeout.ShouldBeGreaterThan(SlowDay);

    [Fact]
    public void The_background_job_gives_a_code_time_for_a_slow_provider_attempt() =>
        new ScanningOptions().JobResolveBudget.ShouldBeGreaterThan(new ProviderResilienceOptions().AttemptTimeout);
}
