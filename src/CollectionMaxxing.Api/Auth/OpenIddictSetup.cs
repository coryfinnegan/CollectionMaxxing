using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CollectionMaxxing.Api.Auth.Pairing;
using CollectionMaxxing.Infrastructure.Identity;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// OpenIddict as the token server of the iPhone app (docs/07): stores, endpoints, lifetimes, keys and validation of bearer tokens.
/// The grants themselves (pairing, refresh) are in <c>Auth/Pairing</c>. The web uses the cookie, not tokens.
/// </summary>
public static class OpenIddictSetup
{
    /// <summary>The custom grant an iPhone uses to turn a QR-code pairing into tokens (see <c>TokenEndpoint</c>).</summary>
    public const string DevicePairingGrant = "urn:cm:device_pairing";

    public const string ApiScope = "api";

    public static IServiceCollection AddTokenServer(this IServiceCollection services)
    {
        services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<AppDbContext>().ReplaceDefaultEntities<Guid>())
            .AddServer(server =>
            {
                server.SetTokenEndpointUris("/connect/token").SetRevocationEndpointUris("/connect/revoke");
                server.AllowCustomFlow(DevicePairingGrant).AllowRefreshTokenFlow();
                server.RegisterScopes(ApiScope);

                // The phone is a public client that proves itself with the pairing token, not a client id.
                server.AcceptAnonymousClients();

                // 10-minute access tokens; refresh tokens rotate and last 90 days from last use (docs/07, "iPhone: pair once").
                server.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));
                server.SetRefreshTokenLifetime(TimeSpan.FromDays(90));

                // A refresh token works once. Using it a second time, even straight away, is a theft or a bug, and revokes the
                // device's whole chain (OpenIddict's default allows 30 seconds of grace for a lost response; docs/07 allows none).
                server.SetRefreshTokenReuseLeeway(TimeSpan.Zero);

                // Revoking a refresh token unpairs the whole phone, not just that token.
                server.AddEventHandler(RevokeDeviceOnRevocation.Descriptor);

                // The API listens on 127.0.0.1 behind Tailscale Serve, which terminates TLS, so OpenIddict can't see HTTPS itself.
                server.UseAspNetCore().EnableTokenEndpointPassthrough().DisableTransportSecurityRequirement();
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseAspNetCore();

                // Check the phone's authorization on every request, so revoking it ends its access token now, not in ten minutes.
                validation.EnableAuthorizationEntryValidation();
                validation.EnableTokenEntryValidation();
            });

        // OpenIddict's trace and debug lines print the tokens it creates and reads, and Information lines print whole requests (which
        // include the pairing token and code). Nothing from it below Warning is worth the risk of a secret in a log (docs/08).
        services.AddLogging(logging => logging.AddFilter("OpenIddict", LogLevel.Warning));

        // Keys are loaded when the server is first used, not at start-up, so tools that start the app without a server (OpenAPI
        // generation, `dotnet ef`) don't need them.
        services.AddOptions<OpenIddictServerOptions>().Configure<IOptions<AuthOptions>, IHostEnvironment>(AddKeys);
        return services;
    }

    private static void AddKeys(OpenIddictServerOptions options, IOptions<AuthOptions> auth, IHostEnvironment environment)
    {
        var keys = auth.Value.Keys;
        var signing = Load(keys.SigningCertificatePath, keys.CertificatePassword)
            ?? DevelopmentOnly(environment, auth.Value, "openiddict-signing.pfx", "CollectionMaxxing token signing");
        var encryption = Load(keys.EncryptionCertificatePath, keys.CertificatePassword)
            ?? DevelopmentOnly(environment, auth.Value, "openiddict-encryption.pfx", "CollectionMaxxing token encryption");

        options.SigningCredentials.Add(new SigningCredentials(new X509SecurityKey(signing), SecurityAlgorithms.RsaSha256));
        options.EncryptionCredentials.Add(new EncryptingCredentials(new X509SecurityKey(encryption), SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
    }

    private static X509Certificate2? Load(string? path, string? password) =>
        string.IsNullOrWhiteSpace(path) ? null : X509CertificateLoader.LoadPkcs12FromFile(path, password);

    /// <summary>
    /// In development the keys are generated once into the (git-ignored) secrets folder, so tokens survive a restart. Anywhere else
    /// the PFX files must be provided (docs/07, "Keys and secrets"): a server must not invent its own keys quietly.
    /// </summary>
    private static X509Certificate2 DevelopmentOnly(IHostEnvironment environment, AuthOptions auth, string fileName, string subject)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"Auth:Keys:SigningCertificatePath and Auth:Keys:EncryptionCertificatePath must point to PFX files outside Development ({fileName} is missing).");
        }

        Directory.CreateDirectory(auth.SecretsPath);
        var path = Path.Combine(auth.SecretsPath, fileName);
        if (!File.Exists(path))
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
            File.WriteAllBytes(path, created.Export(X509ContentType.Pfx));
        }

        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }
}
