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

    public ProductionHost(Dictionary<string, string?>? settings = null, string environment = "Production", string? webRoot = null)
    {
        Directory.CreateDirectory(_root);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            if (webRoot is not null)
            {
                builder.UseWebRoot(webRoot);
            }

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:collectionmaxxing"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1;Command Timeout=1",
                    ["Auth:SecretsPath"] = Path.Combine(_root, "secrets"),
                    ["Auth:KeyRing:Path"] = Path.Combine(_root, "keys"),
                    ["Auth:Setup:IssueOnStartup"] = "false",
                    ["Auth:Auth0:Domain"] = "tenant.auth0.test",
                    ["Auth:Auth0:Audience"] = "urn:collectionmaxxing:api",
                    ["Auth:Auth0:WebClientId"] = "web-client-id",
                    ["Auth:Auth0:WebClientSecret"] = "web-client-secret-for-tests",
                    ["Auth:PublicOrigin"] = "https://pc.tailnet.ts.net",
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

    public HttpClient Client(Guid? userId = null, bool followRedirects = true)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = followRedirects });
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
