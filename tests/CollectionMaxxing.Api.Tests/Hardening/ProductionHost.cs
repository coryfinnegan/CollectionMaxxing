using CollectionMaxxing.Api.Tests.RateLimiting;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Hardening;

/// <summary>
/// The real API in the Production environment, with the header-based test sign-in. Its database address points nowhere, so any call that
/// needs the database fails the way an outage would (a 500) and the pages that don't need it work. Settings can be added per test.
/// </summary>
public sealed class ProductionHost : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-hardening-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public ProductionHost(Dictionary<string, string?>? settings = null, string environment = "Production")
    {
        Directory.CreateDirectory(_root);
        var signing = MakeCertificate("signing");
        var encryption = MakeCertificate("encryption");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:collectionmaxxing"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1;Command Timeout=1",
                    ["Auth:SecretsPath"] = Path.Combine(_root, "secrets"),
                    ["Auth:KeyRing:Path"] = Path.Combine(_root, "keys"),
                    ["Auth:Setup:IssueOnStartup"] = "false",
                    ["Auth:Keys:SigningCertificatePath"] = signing,
                    ["Auth:Keys:EncryptionCertificatePath"] = encryption,
                };
                foreach (var (key, value) in settings ?? [])
                {
                    values[key] = value;
                }

                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, PeerFilter>();
                foreach (var worker in services.Where(d => d.ImplementationType == typeof(JobWorker)).ToList())
                {
                    services.Remove(worker);
                }

                services
                    .AddAuthentication(options =>
                    {
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        });
    }

    private string MakeCertificate(string name)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=test " + name, rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(_root, name + ".pfx");
        File.WriteAllBytes(path, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
        return path;
    }

    public HttpClient Client(Guid? userId = null)
    {
        var client = _factory.CreateClient();
        if (userId is { } id)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, id.ToString());
        }

        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
