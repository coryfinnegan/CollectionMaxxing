using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Tests.Support;

/// <summary>
/// The test sign-in: the caller's user id travels in the <c>X-Test-User</c> header, and any platform roles (e.g. <c>admin</c>,
/// comma separated) in <c>X-Test-Roles</c>. No user header means anonymous.
/// Real sign-in (Auth0, through a fake tenant) is covered by the tests in Auth/ with <c>AuthApp</c>; every other test keeps using this one.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";

    /// <summary>When the test user last signed in, in seconds since 1970. Absent means "just now".</summary>
    public const string AuthTimeHeader = "X-Test-Auth-Time";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var value))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Every test user "just signed in", so endpoints that need a recent sign-in let them through.
        // The re-auth tests use real sign-in instead (see Auth/AuthApp).
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, value.ToString()),
            new("auth_time", Request.Headers.TryGetValue(AuthTimeHeader, out var authTime) ? authTime.ToString() : time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        };
        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(role => new Claim(ClaimTypes.Role, role)));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
