using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Placeholder authentication until real sign-in lands (passkeys, pairing): it never authenticates anyone, so every
/// protected endpoint answers 401. Tests replace it with a scheme that reads the user id from a header.
/// </summary>
public sealed class NoSignInHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "NoSignIn";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
}
