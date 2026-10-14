using CollectionMaxxing.Application.Jobs;

namespace CollectionMaxxing.Application.Tests.Jobs;

public class JobRetryPolicyTests
{
    private readonly JobRetryPolicy _policy = new() { MaxAttempts = 4, BaseDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(60) };

    [Fact]
    public void Delays_double_after_each_failed_attempt()
    {
        _policy.DelayAfter(1).ShouldBe(TimeSpan.FromSeconds(10));
        _policy.DelayAfter(2).ShouldBe(TimeSpan.FromSeconds(20));
        _policy.DelayAfter(3).ShouldBe(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void Delays_stop_growing_at_the_cap()
    {
        _policy.DelayAfter(4).ShouldBe(TimeSpan.FromSeconds(60));
        _policy.DelayAfter(500).ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void A_job_is_retried_until_it_has_used_all_its_attempts()
    {
        _policy.ShouldRetry(3).ShouldBeTrue();
        _policy.ShouldRetry(4).ShouldBeFalse();
    }
}
