using System.Text.Json;
using CollectionMaxxing.Application.Accounts;
using Microsoft.AspNetCore.DataProtection;

namespace CollectionMaxxing.Api.Auth;

/// <summary>What the server remembers between handing out WebAuthn options and receiving the browser's answer.</summary>
/// <param name="Kind">Whether the browser is creating a passkey or signing in with one.</param>
/// <param name="Plan">For a creation: who the passkey is for and what allowed it. Null for a sign-in.</param>
/// <param name="State">The challenge and options, as ASP.NET Core Identity's passkey handler needs them back.</param>
/// <param name="IssuedAt">Seconds since 1970, so an old ceremony can't be finished later.</param>
public sealed record Ceremony(CeremonyKind Kind, CeremonyPlan? Plan, string State, long IssuedAt);

public enum CeremonyKind
{
    Create = 1,
    SignIn = 2,
}

/// <summary>
/// The ceremony, sealed in a short-lived cookie with ASP.NET Data Protection (so the browser can neither read nor change it).
/// Without this cookie <c>POST /account/passkey/register</c> and <c>POST /account/login</c> have nothing to finish, which is what
/// makes "a code, token or recent sign-in first" a rule the server enforces rather than a convention.
/// </summary>
public sealed class CeremonyCookie(IDataProtectionProvider dataProtection, TimeProvider time)
{
    public const string Name = "__Host-cm-ceremony";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector = dataProtection.CreateProtector("CollectionMaxxing.Ceremony.v1");

    public void Write(HttpContext context, CeremonyKind kind, CeremonyPlan? plan, string state)
    {
        var ceremony = new Ceremony(kind, plan, state, time.GetUtcNow().ToUnixTimeSeconds());
        context.Response.Cookies.Append(Name, _protector.Protect(JsonSerializer.Serialize(ceremony)), Options());
    }

    /// <summary>The ceremony this browser started, or null if there is none, it was tampered with, or it timed out.</summary>
    public Ceremony? Read(HttpContext context, CeremonyKind kind)
    {
        if (!context.Request.Cookies.TryGetValue(Name, out var sealedValue))
        {
            return null;
        }

        try
        {
            var ceremony = JsonSerializer.Deserialize<Ceremony>(_protector.Unprotect(sealedValue));
            var age = time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(ceremony?.IssuedAt ?? 0);
            return ceremony is not null && ceremony.Kind == kind && age >= TimeSpan.Zero && age <= Lifetime ? ceremony : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
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
        SameSite = SameSiteMode.Strict,
        Path = "/",
        MaxAge = Lifetime,
        IsEssential = true,
    };
}
