namespace CollectionMaxxing.Infrastructure.Identity;

/// <summary>Settings under <c>Auth:</c>.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>The server's secrets folder: the setup code, and the signing and encryption keys. Git-ignored.</summary>
    public string SecretsPath { get; set; } = "secrets";

    /// <summary>The WebAuthn relying-party id: the host name people browse to (your Tailscale name). It can't change without losing every passkey.</summary>
    public string RelyingPartyId { get; set; } = "localhost";

    public SetupOptions Setup { get; set; } = new();

    public RateLimitOptions RateLimit { get; set; } = new();

    public KeyOptions Keys { get; set; } = new();

    public sealed class SetupOptions
    {
        /// <summary>Make and print a setup code when the server starts with no owner. Off in tests.</summary>
        public bool IssueOnStartup { get; set; } = true;
    }

    public sealed class RateLimitOptions
    {
        /// <summary>Attempts per window, per caller, on the endpoints that take a code or a passkey.</summary>
        public int PermitLimit { get; set; } = 10;

        public int WindowSeconds { get; set; } = 60;
    }

    public sealed class KeyOptions
    {
        /// <summary>PFX files for OpenIddict's signing and encryption keys. Required outside Development.</summary>
        public string? SigningCertificatePath { get; set; }

        public string? EncryptionCertificatePath { get; set; }

        public string? CertificatePassword { get; set; }
    }
}
