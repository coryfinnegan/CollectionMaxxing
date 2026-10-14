using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth.Pairing;

/// <summary>
/// Limits how fast a caller can try to pair, and locks out a caller who guesses.
/// <list type="bullet">
/// <item>At most <c>Auth:RateLimit:PermitLimit</c> pairing attempts per window per caller, right or wrong. The caller is the client
/// address from <see cref="ClientAddressResolver"/> (the forwarded address only behind a trusted proxy; otherwise everyone behind a
/// proxy shares one bucket, as for the other sign-in endpoints).</item>
/// <item>A caller with <see cref="PairingSecrets.MaxWrongGuesses"/> wrong 6-digit codes in a window is refused outright (token or
/// code) until the window passes. The wrong guesses are held against the caller, never against the pairing, so a stranger can't
/// burn the owner's open pairing. A token is 256 bits and can't be guessed, so a wrong token costs only the attempt.</item>
/// </list>
/// It counts only the pairing grant, not token refreshes, which a phone in use makes all the time. That is why this isn't the ASP.NET
/// rate limiter: that one decides before the request body is read, and the grant type is in the body.
/// </summary>
public sealed class PairingAttemptLimiter(IOptions<AuthOptions> options, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _windows = [];
    private readonly Dictionary<string, Window> _wrongGuesses = [];

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

    /// <summary>Whether this caller has used up its wrong guesses for the current window.</summary>
    public bool IsLockedOut(string caller)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            return _wrongGuesses.TryGetValue(caller, out var window)
                && now - window.Start < WindowLength()
                && window.Count >= PairingSecrets.MaxWrongGuesses;
        }
    }

    /// <summary>Holds one wrong 6-digit code against the caller.</summary>
    public void RecordWrongCode(string caller)
    {
        var length = WindowLength();
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_wrongGuesses.Count > 1024)
            {
                foreach (var stale in _wrongGuesses.Where(w => now - w.Value.Start >= length).Select(w => w.Key).ToList())
                {
                    _wrongGuesses.Remove(stale);
                }
            }

            if (!_wrongGuesses.TryGetValue(caller, out var window) || now - window.Start >= length)
            {
                window = new Window(now, 0);
            }

            _wrongGuesses[caller] = window with { Count = Math.Min(window.Count + 1, PairingSecrets.MaxWrongGuesses) };
        }
    }

    private TimeSpan WindowLength() => TimeSpan.FromSeconds(Math.Max(1, options.Value.RateLimit.WindowSeconds));

    private readonly record struct Window(DateTimeOffset Start, int Count);
}
