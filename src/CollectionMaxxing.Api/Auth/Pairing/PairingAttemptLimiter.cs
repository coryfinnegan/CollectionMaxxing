using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth.Pairing;

/// <summary>
/// Limits how fast a caller can try to pair: at most <c>Auth:RateLimit:PermitLimit</c> pairing attempts per window per caller address,
/// right or wrong. (Behind Tailscale Serve everyone arrives from 127.0.0.1, so this is one bucket for the household, as for the
/// other sign-in endpoints.) It counts only the pairing grant, not token refreshes, which a phone in use makes all the time. That is why
/// this isn't the ASP.NET rate limiter: that one decides before the request body is read, and the grant type is in the body.
/// </summary>
public sealed class PairingAttemptLimiter(IOptions<AuthOptions> options, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _windows = [];

    /// <summary>Counts one attempt. Returns false, and how long to wait, when the caller has used up the window.</summary>
    public bool TryAcquire(string caller, out TimeSpan retryAfter)
    {
        var settings = options.Value.RateLimit;
        var length = TimeSpan.FromSeconds(Math.Max(1, settings.WindowSeconds));
        var now = time.GetUtcNow();

        lock (_gate)
        {
            if (_windows.Count > 1024)
            {
                foreach (var stale in _windows.Where(w => now - w.Value.Start >= length).Select(w => w.Key).ToList())
                {
                    _windows.Remove(stale);
                }
            }

            if (!_windows.TryGetValue(caller, out var window) || now - window.Start >= length)
            {
                window = new Window(now, 0);
            }

            if (window.Count >= settings.PermitLimit)
            {
                _windows[caller] = window;
                retryAfter = window.Start + length - now;
                return false;
            }

            _windows[caller] = window with { Count = window.Count + 1 };
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private readonly record struct Window(DateTimeOffset Start, int Count);
}
