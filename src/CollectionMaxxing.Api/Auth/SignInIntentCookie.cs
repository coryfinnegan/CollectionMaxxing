using System.Security.Cryptography;
using System.Text.Json;
using CollectionMaxxing.Application.Accounts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace CollectionMaxxing.Api.Auth;

/// <summary>What the browser set out to do, as the server remembers it across the Auth0 round trip.</summary>
/// <param name="Id">Random. The Auth0 <c>state</c> carries only this, and the callback must bring back the same one.</param>
/// <param name="Intent">The purpose, and what allowed it (a code, an invite token, the signed-in user).</param>
/// <param name="ReturnUrl">A local path to go back to.</param>
/// <param name="UserId">Who was signed in on this browser when it left, if anyone.</param>
/// <param name="WebSessionId">Their session, which a new sign-in replaces.</param>
/// <param name="IssuedAt">Seconds since 1970, so an old intent can't be used later.</param>
public sealed record SealedIntent(string Id, SignInIntent Intent, string ReturnUrl, Guid? UserId, Guid? WebSessionId, long IssuedAt);

/// <summary>
/// The sign-in intent, sealed with Data Protection in a short-lived cookie, so the browser can neither read nor change it and it never
/// travels in Auth0's <c>state</c> alone. SameSite=Lax: Auth0 sends the browser back with a top-level GET, which Lax lets through and
/// Strict would not.
/// </summary>
public sealed class SignInIntentCookie(IDataProtectionProvider dataProtection, TimeProvider time)
{
    public const string Name = "__Host-cm-signin";

    /// <summary>The key in the challenge's properties (and so in Auth0's protected <c>state</c>) that names the intent.</summary>
    public const string StateKey = "cm.intent";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector = dataProtection.CreateProtector("CollectionMaxxing.SignInIntent.v1");

    public SealedIntent Write(HttpContext context, SignInIntent intent, string returnUrl, Guid? userId = null, Guid? webSessionId = null)
    {
        var sealedIntent = new SealedIntent(
            WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            intent,
            returnUrl,
            userId,
            webSessionId,
            time.GetUtcNow().ToUnixTimeSeconds());
        context.Response.Cookies.Append(Name, _protector.Protect(JsonSerializer.Serialize(sealedIntent)), Options());
        return sealedIntent;
    }

    /// <summary>The intent this browser sealed, or null if there is none, it was tampered with, or it timed out.</summary>
    public SealedIntent? Read(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(Name, out var sealedValue))
        {
            return null;
        }

        try
        {
            var intent = JsonSerializer.Deserialize<SealedIntent>(_protector.Unprotect(sealedValue));
            var age = time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(intent?.IssuedAt ?? 0);
            return intent is not null && age >= TimeSpan.Zero && age <= Lifetime ? intent : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Clear(HttpContext context) => context.Response.Cookies.Delete(Name, Options());

    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = Lifetime,
        IsEssential = true,
    };
}
