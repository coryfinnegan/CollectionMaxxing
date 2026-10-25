namespace CollectionMaxxing.Infrastructure.Providers.Resilience;

/// <summary>
/// How a provider call admits its retries. Polly re-sends the same <see cref="HttpRequestMessage"/>, so a retry is a
/// second upstream request: it waits for its own rate-limiter slot and spends its own quota token, exactly like the
/// first attempt did. The first attempt is admitted by <see cref="HttpCatalogProvider"/> itself (outside the attempt
/// timeout, with the quota-denied answer); this admission only runs from the second send of the message on.
/// </summary>
internal sealed class ProviderRetryAdmission(string provider, TimeSpan interval, Func<CancellationToken, Task<bool>>? admit)
{
    internal static readonly HttpRequestOptionsKey<ProviderRetryAdmission> OptionKey = new("CollectionMaxxing.Providers.RetryAdmission");

    public string Provider { get; } = provider;

    public TimeSpan Interval { get; } = interval;

    public Func<CancellationToken, Task<bool>>? Admit { get; } = admit;

    /// <summary>True once the first attempt has passed through the handler; the next send is a retry.</summary>
    public bool FirstAttemptSent { get; set; }
}

/// <summary>
/// Admits every retry a provider call makes. Registered inside the resilience pipeline (after it), so it runs once per
/// upstream request rather than once per logical call. A request without admission (anything not sent through
/// <see cref="HttpCatalogProvider"/>) passes straight through.
/// </summary>
internal sealed class ProviderRetryAdmissionHandler(ProviderRateLimiters limiters) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(ProviderRetryAdmission.OptionKey, out var admission) && admission is not null)
        {
            if (admission.FirstAttemptSent)
            {
                await limiters.For(admission.Provider, admission.Interval).WaitAsync(cancellationToken);
                if (admission.Admit is not null && !await admission.Admit(cancellationToken))
                {
                    throw new ProviderQuotaExhaustedException(admission.Provider);
                }
            }

            admission.FirstAttemptSent = true;
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// A retry was refused: the day's quota ran out between attempts. Never retried (see the pipeline's
/// <c>ShouldHandle</c>) and never leaves the provider layer: <see cref="HttpCatalogProvider"/> answers Denied.
/// </summary>
internal sealed class ProviderQuotaExhaustedException(string provider)
    : Exception($"The daily quota for {provider} ran out during a retry.")
{
    public string Provider { get; } = provider;
}
