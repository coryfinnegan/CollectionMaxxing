namespace CollectionMaxxing.Infrastructure.Identity;

/// <summary>
/// Settings under <c>Auth:</c>. The names under <c>Auth:Auth0</c> and <c>Auth:PublicOrigin</c> are the config contract of issue #226.
/// Old settings that may still be passed (<c>Auth:RelyingPartyId</c>, <c>Auth:Keys:*</c>) are ignored.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>The server's secrets folder: the setup code. Git-ignored.</summary>
    public string SecretsPath { get; set; } = "secrets";

    /// <summary>
    /// The address people browse to, for example <c>https://pc.tailnet.ts.net</c>. The sign-in callback and the sign-out return address
    /// are built from it, never from a request's Host header.
    /// </summary>
    public string? PublicOrigin { get; set; }

    public Auth0Options Auth0 { get; set; } = new();

    public SetupOptions Setup { get; set; } = new();

    public RateLimitOptions RateLimit { get; set; } = new();

    /// <summary><see cref="PublicOrigin"/> without a trailing slash, or null when it isn't set.</summary>
    public string? PublicOriginOrNull =>
        string.IsNullOrWhiteSpace(PublicOrigin) ? null : PublicOrigin.Trim().TrimEnd('/');

    public sealed class Auth0Options
    {
        /// <summary>The tenant's domain, for example <c>tenant.us.auth0.com</c>. Tokens must come from <c>https://{Domain}/</c>.</summary>
        public string? Domain { get; set; }

        /// <summary>The API's identifier at Auth0 (<c>urn:collectionmaxxing:api</c>). An access token must name it.</summary>
        public string? Audience { get; set; }

        /// <summary>The web app's client (a confidential Regular Web Application): the API runs its code flow.</summary>
        public string? WebClientId { get; set; }

        public string? WebClientSecret { get; set; }

        /// <summary>For the Management API (AUTH-2). Not used yet.</summary>
        public string? ManagementClientId { get; set; }

        public string? ManagementClientSecret { get; set; }
    }

    public sealed class SetupOptions
    {
        /// <summary>Make and print a setup code when the server starts with no owner. Off in tests.</summary>
        public bool IssueOnStartup { get; set; } = true;
    }

    public sealed class RateLimitOptions
    {
        /// <summary>Attempts per window, per caller, on the endpoints that take a code or start a sign-in.</summary>
        public int PermitLimit { get; set; } = 10;

        public int WindowSeconds { get; set; } = 60;
    }
}
